using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Dapper;
using EZEHR.Access;
using EZEHR.Domain;
using EZEHR.Records;
using EZEHR.Security.Pki;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Api;

public class NetworkIngestTests : IDisposable
{
    private readonly EzehrFactory app = new();
    private readonly X509Certificate2 maple;
    private readonly X509Certificate2 lakeside;
    private readonly Guid sam;

    public NetworkIngestTests()
    {
        maple = app.Pki.Issue("maple.providers.ezehr");
        lakeside = app.Pki.Issue("lakeside.providers.ezehr");
        sam = app.With(sp => sp.GetRequiredService<RecordStore>().CreatePatientAsync("Sam Taylor").GetAwaiter().GetResult());
        app.With(sp => sp.GetRequiredService<SqliteConnection>().Execute(
            "INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id) VALUES (@p, 'urn:provider:maple', 'MSF-0932')",
            new { p = sam.ToString() }));
    }

    public void Dispose() => app.Dispose();

    private string Signed(X509Certificate2 cert, string id, int egfr) => RecordSigner.Sign(
        JsonSerializer.SerializeToElement(new
        {
            resourceType = "Observation", id, status = "final",
            code = new { coding = new[] { new { system = "http://loinc.org", code = "98979-8" } } },
            valueQuantity = new { value = egfr },
        }),
        cert, [], app.Pki.Clock.GetUtcNow());

    private Task<HttpResponseMessage> Push(X509Certificate2? cert, string patientId, params string[] entries) =>
        app.Client(cert).PostAsJsonAsync("/api/network/records", new { providerPatientId = patientId, entries });

    private int EntryCount() => app.With(sp => sp.GetRequiredService<SqliteConnection>().ExecuteScalar<int>("SELECT COUNT(*) FROM entries"));

    [Fact]
    public async Task Ingest_stores_signed_entries()
    {
        var res = await Push(maple, "MSF-0932", Signed(maple, "egfr-1", 96), Signed(maple, "egfr-2", 90));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("inserted").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());
        Assert.Equal(2, EntryCount());

        var events = app.With(sp => sp.GetRequiredService<AuditLog>().List(sam));
        var ingest = Assert.Single(events);
        Assert.Equal("Ingest", ingest.Action);
        Assert.True(ingest.Granted);
        Assert.Equal("urn:provider:maple", ingest.Actor!.SubjectUri);
    }

    [Fact] // Review Focus 4
    public async Task Ingest_twice_is_idempotent()
    {
        var entries = new[] { Signed(maple, "egfr-1", 96), Signed(maple, "egfr-2", 90) };
        await Push(maple, "MSF-0932", entries);
        app.Pki.Clock.Advance(TimeSpan.FromMinutes(5));
        var res = await Push(maple, "MSF-0932", Signed(maple, "egfr-1", 96), Signed(maple, "egfr-2", 90)); // re-signed: new iat, same ids
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("inserted").GetInt32());
        Assert.Equal(2, body.GetProperty("skipped").GetInt32());
        Assert.Equal(2, EntryCount());
    }

    [Fact]
    public async Task Ingest_rejects_entry_signed_by_other_provider()
    {
        var res = await Push(maple, "MSF-0932", Signed(maple, "egfr-1", 96), Signed(lakeside, "egfr-x", 40));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, EntryCount());
    }

    [Fact]
    public async Task Ingest_rejects_bad_signature_and_missing_id()
    {
        var good = Signed(maple, "egfr-1", 96);
        var res = await Push(maple, "MSF-0932", good[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var noId = RecordSigner.Sign(JsonDocument.Parse("""{"resourceType":"Observation","status":"final"}""").RootElement, maple, [], app.Pki.Clock.GetUtcNow());
        res = await Push(maple, "MSF-0932", noId);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, EntryCount());
    }

    [Fact]
    public async Task Ingest_unlinked_patient_409()
    {
        var res = await Push(lakeside, "LUC-20417", Signed(lakeside, "egfr-1", 81));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(0, EntryCount());
    }

    [Fact]
    public async Task No_certificate_401()
    {
        var res = await Push(null, "MSF-0932", Signed(maple, "egfr-1", 96));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoCertificate", body.GetProperty("error").GetString());
        Assert.Equal(0, EntryCount());
        Assert.Empty(app.With(sp => sp.GetRequiredService<AuditLog>().List(sam)));
    }

    [Fact]
    public async Task Self_signed_certificate_401()
    {
        var broker = TestPki.SelfSigned("exchange.broker.example", app.Pki.Clock.GetUtcNow());
        var res = await Push(broker, "MSF-0932", Signed(maple, "egfr-1", 96));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UntrustedCertificate", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_org_with_trusted_chain_401()
    {
        var mallory = app.Pki.Issue("mallory.providers.ezehr");
        var res = await Push(mallory, "MSF-0932", Signed(mallory, "egfr-1", 96));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task App_certificate_cannot_ingest_403()
    {
        var kidney = app.Pki.Issue("kidney-check.apps.ezehr");
        var res = await Push(kidney, "MSF-0932", Signed(kidney, "egfr-1", 96));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(0, EntryCount());
    }

    [Fact]
    public async Task Provider_creates_invite_and_patient_redeems_it_once()
    {
        var res = await app.Client(lakeside).PostAsJsonAsync("/api/network/invites", new { providerPatientId = "LUC-20417" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.GetProperty("code").GetString()!;
        Assert.Equal(8, code.Length);
        Assert.Equal($"https://ezehr.test/Patient/Enroll?invite={code}", body.GetProperty("url").GetString());

        app.With(sp => { sp.GetRequiredService<InviteService>().Redeem(code, sam); return 0; });
        var again = Assert.Throws<InviteException>(() => app.With(sp => { sp.GetRequiredService<InviteService>().Redeem(code, sam); return 0; }));
        Assert.Equal("invite_used", again.Code);

        var push = await Push(lakeside, "LUC-20417", Signed(lakeside, "egfr-3", 81));
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
    }

    [Fact]
    public async Task Invite_expires_after_7_days()
    {
        var res = await app.Client(lakeside).PostAsJsonAsync("/api/network/invites", new { providerPatientId = "LUC-20417" });
        var code = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        app.Pki.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        var e = Assert.Throws<InviteException>(() => app.With(sp => { sp.GetRequiredService<InviteService>().Redeem(code, sam); return 0; }));
        Assert.Equal("invite_expired", e.Code);
    }

    [Fact]
    public async Task App_cannot_create_invite_403()
    {
        var kidney = app.Pki.Issue("kidney-check.apps.ezehr");
        var res = await app.Client(kidney).PostAsJsonAsync("/api/network/invites", new { providerPatientId = "X" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
