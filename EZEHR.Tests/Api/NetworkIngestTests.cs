using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dapper;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Api;

public class NetworkIngestTests : IAsyncLifetime
{
    private EzehrFactory _app = null!;
    private X509Certificate2 _maple = null!;
    private X509Certificate2 _lakeside = null!;
    private Guid _sam;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _maple = _app.Pki.Issue(OrgDirectory.ProviderCn("maple"));
        _lakeside = _app.Pki.Issue(OrgDirectory.ProviderCn("lakeside"));
        _sam = await _app.WithAsync(services =>
            services.GetRequiredService<RecordManager>().CreatePatientAsync("Sam Taylor")
        );
        await using SqlConnection connection = await _app.Database.Db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id)
            VALUES (@patientId, 'urn:provider:maple', 'MSF-0932')
            """,
            new { patientId = _sam }
        );
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private string Signed(X509Certificate2 certificate, string id, int egfr)
    {
        JsonElement lab = JsonSerializer.SerializeToElement(
            new
            {
                resourceType = "Observation",
                id,
                status = "final",
                code = new
                {
                    coding = new[] { new { system = "http://loinc.org", code = "98979-8" } },
                },
                valueQuantity = new { value = egfr },
            }
        );
        return RecordSigner.Sign(lab, certificate, [], _app.Pki.Clock.GetUtcNow());
    }

    private Task<HttpResponseMessage> PushAsync(
        X509Certificate2? certificate,
        string providerPatientId,
        params string[] entries
    )
    {
        return _app.Client(certificate)
            .PostAsJsonAsync("/api/network/records", new { providerPatientId, entries });
    }

    private async Task<int> EntryCountAsync()
    {
        await using SqlConnection connection = await _app.Database.Db.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entries");
    }

    private Task<IReadOnlyList<AuditEvent>> EventsAsync()
    {
        return _app.WithAsync(services =>
            services.GetRequiredService<AuditManager>().ListAsync(_sam)
        );
    }

    private Task<InviteResult> RedeemInviteAsync(string code)
    {
        return _app.WithAsync(services =>
            services.GetRequiredService<InviteManager>().RedeemAsync(code, _sam)
        );
    }

    private async Task<string> CreateInviteAsync(string providerPatientId)
    {
        HttpResponseMessage response = await _app.Client(_lakeside)
            .PostAsJsonAsync("/api/network/invites", new { providerPatientId });
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task Ingest_stores_signed_entries()
    {
        HttpResponseMessage response = await PushAsync(
            _maple,
            "MSF-0932",
            Signed(_maple, "egfr-1", 96),
            Signed(_maple, "egfr-2", 90)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("inserted").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());
        Assert.Equal(2, await EntryCountAsync());

        AuditEvent ingest = Assert.Single(await EventsAsync());
        Assert.Equal("Ingest", ingest.Action);
        Assert.True(ingest.Granted);
        Assert.Equal("urn:provider:maple", ingest.Actor!.SubjectUri);
    }

    [Fact] // Review Focus 4
    public async Task Ingest_twice_is_idempotent()
    {
        await PushAsync(
            _maple,
            "MSF-0932",
            Signed(_maple, "egfr-1", 96),
            Signed(_maple, "egfr-2", 90)
        );
        _app.Pki.Clock.Advance(TimeSpan.FromMinutes(5));
        // Re-signed: a new iat, the same ids.
        HttpResponseMessage response = await PushAsync(
            _maple,
            "MSF-0932",
            Signed(_maple, "egfr-1", 96),
            Signed(_maple, "egfr-2", 90)
        );
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("inserted").GetInt32());
        Assert.Equal(2, body.GetProperty("skipped").GetInt32());
        Assert.Equal(2, await EntryCountAsync());
    }

    [Fact]
    public async Task Source_keys_are_case_sensitive()
    {
        HttpResponseMessage response = await PushAsync(
            _maple,
            "MSF-0932",
            Signed(_maple, "egfr-a", 96),
            Signed(_maple, "egfr-A", 90)
        );
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("inserted").GetInt32());
        Assert.Equal(2, await EntryCountAsync());
    }

    [Fact]
    public async Task Ingest_rejects_entry_signed_by_other_provider()
    {
        HttpResponseMessage response = await PushAsync(
            _maple,
            "MSF-0932",
            Signed(_maple, "egfr-1", 96),
            Signed(_lakeside, "egfr-x", 40)
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await EntryCountAsync());
    }

    [Fact]
    public async Task Ingest_rejects_bad_signature_and_missing_or_long_id()
    {
        string good = Signed(_maple, "egfr-1", 96);
        HttpResponseMessage response = await PushAsync(_maple, "MSF-0932", good[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        JsonElement noIdResource = JsonDocument
            .Parse("""{"resourceType":"Observation","status":"final"}""")
            .RootElement;
        string noId = RecordSigner.Sign(noIdResource, _maple, [], _app.Pki.Clock.GetUtcNow());
        response = await PushAsync(_maple, "MSF-0932", noId);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        response = await PushAsync(_maple, "MSF-0932", Signed(_maple, new string('a', 65), 96));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await EntryCountAsync());
    }

    [Fact]
    public async Task Ingest_unlinked_patient_409()
    {
        HttpResponseMessage response = await PushAsync(
            _lakeside,
            "LUC-20417",
            Signed(_lakeside, "egfr-1", 81)
        );
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await EntryCountAsync());
    }

    [Fact]
    public async Task No_certificate_401()
    {
        HttpResponseMessage response = await PushAsync(
            null,
            "MSF-0932",
            Signed(_maple, "egfr-1", 96)
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoCertificate", body.GetProperty("error").GetString());
        Assert.Equal(0, await EntryCountAsync());
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task Self_signed_certificate_401()
    {
        X509Certificate2 broker = TestPki.SelfSigned(
            "exchange.broker.example",
            _app.Pki.Clock.GetUtcNow()
        );
        HttpResponseMessage response = await PushAsync(
            broker,
            "MSF-0932",
            Signed(_maple, "egfr-1", 96)
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UntrustedCertificate", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_org_with_trusted_chain_401()
    {
        X509Certificate2 mallory = _app.Pki.Issue(OrgDirectory.ProviderCn("mallory"));
        HttpResponseMessage response = await PushAsync(
            mallory,
            "MSF-0932",
            Signed(mallory, "egfr-1", 96)
        );
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task App_certificate_cannot_ingest_403()
    {
        X509Certificate2 kidney = _app.Pki.Issue(OrgDirectory.KidneyCheckCn);
        HttpResponseMessage response = await PushAsync(
            kidney,
            "MSF-0932",
            Signed(kidney, "egfr-1", 96)
        );
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await EntryCountAsync());
    }

    [Fact]
    public async Task Missing_provider_patient_id_400()
    {
        HttpResponseMessage response = await _app.Client(_maple)
            .PostAsJsonAsync("/api/network/records", new { entries = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Provider_creates_invite_and_patient_redeems_it_once()
    {
        HttpResponseMessage response = await _app.Client(_lakeside)
            .PostAsJsonAsync("/api/network/invites", new { providerPatientId = "LUC-20417" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string code = body.GetProperty("code").GetString()!;
        Assert.Equal(8, code.Length);
        Assert.Equal(
            $"https://ezehr.test/Patient/Enroll?invite={code}",
            body.GetProperty("url").GetString()
        );

        InviteResult first = await RedeemInviteAsync(code);
        Assert.Equal(new ProviderLink("urn:provider:lakeside", "LUC-20417"), first.Link);
        InviteResult again = await RedeemInviteAsync(code);
        Assert.Equal(new InviteResult(false, null, InviteError.InviteUsed), again);

        HttpResponseMessage push = await PushAsync(
            _lakeside,
            "LUC-20417",
            Signed(_lakeside, "egfr-3", 81)
        );
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
    }

    [Fact]
    public async Task Invite_for_patient_linked_elsewhere_is_refused_and_not_used()
    {
        string code = await CreateInviteAsync("MSF-0932");
        await using (SqlConnection connection = await _app.Database.Db.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id)
                VALUES (@patientId, 'urn:provider:lakeside', 'MSF-0932')
                """,
                new { patientId = Guid.NewGuid() }
            );
        }
        InviteResult refused = await RedeemInviteAsync(code);
        Assert.Equal(InviteError.InviteLinkedElsewhere, refused.Error);
        InviteResult peek = await _app.WithAsync(services =>
            services.GetRequiredService<InviteManager>().PeekAsync(code)
        );
        Assert.True(peek.Succeeded);
    }

    [Fact]
    public async Task Invite_expires_after_7_days()
    {
        string code = await CreateInviteAsync("LUC-20417");
        _app.Pki.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        InviteResult expired = await RedeemInviteAsync(code);
        Assert.Equal(InviteError.InviteExpired, expired.Error);
        Assert.Equal(InviteError.InviteUnknown, (await RedeemInviteAsync("ZZZZZZZZ")).Error);
    }

    [Fact]
    public async Task App_cannot_create_invite_403()
    {
        X509Certificate2 kidney = _app.Pki.Issue(OrgDirectory.KidneyCheckCn);
        HttpResponseMessage response = await _app.Client(kidney)
            .PostAsJsonAsync("/api/network/invites", new { providerPatientId = "X" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("WrongPartyKind", body.GetProperty("error").GetString());
    }
}
