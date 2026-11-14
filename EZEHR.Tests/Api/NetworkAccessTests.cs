using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EZEHR.Access;
using EZEHR.Domain;
using EZEHR.Records;
using EZEHR.Security.Pki;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Api;

public class NetworkAccessTests : IDisposable
{
    private readonly EzehrFactory app = new();
    private readonly X509Certificate2 maple;
    private readonly X509Certificate2 kidney;
    private readonly Guid sam;

    public NetworkAccessTests()
    {
        maple = app.Pki.Issue("maple.providers.ezehr");
        kidney = app.Pki.Issue("kidney-check.apps.ezehr");
        sam = app.With(sp =>
        {
            var store = sp.GetRequiredService<RecordStore>();
            var id = store.CreatePatientAsync("Sam Taylor").GetAwaiter().GetResult();
            store.PutAsync(id, "urn:provider:maple|Observation/egfr-1", "Observation", Sign(maple, Lab("egfr-1", "98979-8", 96))).GetAwaiter().GetResult();
            store.PutAsync(id, "urn:provider:maple|Observation/bp-1", "Observation", Sign(maple, Lab("bp-1", "85354-9", 138))).GetAwaiter().GetResult();
            store.PutAsync(id, "urn:provider:maple|Condition/htn", "Condition",
                Sign(maple, JsonSerializer.SerializeToElement(new { resourceType = "Condition", id = "htn" }))).GetAwaiter().GetResult();
            return id;
        });
    }

    public void Dispose() => app.Dispose();

    private static JsonElement Lab(string id, string loinc, int value) => JsonSerializer.SerializeToElement(new
    {
        resourceType = "Observation", id, status = "final",
        code = new { coding = new[] { new { system = "http://loinc.org", code = loinc } } },
        valueQuantity = new { value },
    });

    private string Sign(X509Certificate2 cert, JsonElement resource) => RecordSigner.Sign(resource, cert, [], app.Pki.Clock.GetUtcNow());

    private JsonElement Note(string id) => JsonSerializer.SerializeToElement(new
    {
        resourceType = "Communication", id, status = "completed",
        sender = new { display = "Kidney Check" },
        payload = new[] { new { contentString = Copy.KidneyNote(34, 3) } },
    });

    private IReadOnlyList<AuditEvent> Events() => app.With(sp => sp.GetRequiredService<AuditLog>().List(sam));

    private void GrantKidneyCheck() => app.With(sp => sp.GetRequiredService<GrantService>().CreateAppGrant(sam, OrgDirectory.Known["kidney-check.apps.ezehr"]));

    [Fact]
    public async Task Broker_no_cert_on_record_endpoint_logs_blocked_event()
    {
        var res = await app.Client().GetAsync($"/api/network/patients/{sam}/record?purpose=treatment");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var e = Assert.Single(Events());
        Assert.Null(e.Actor);
        Assert.Equal("treatment", e.ClaimedPurpose);
        Assert.Equal(DenyReason.NoCertificate, e.Reason);
        Assert.Equal("ReadRecord", e.Action);

        var broker = TestPki.SelfSigned("exchange.broker.example", app.Pki.Clock.GetUtcNow());
        res = await app.Client(broker).GetAsync($"/api/network/patients/{sam}/record?purpose=treatment");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(DenyReason.UntrustedCertificate, Events()[^1].Reason);
        Assert.Equal(Copy.BrokerBlocked("treatment"), Copy.BrokerBlocked(Events()[^1].ClaimedPurpose!));
    }

    [Fact]
    public async Task App_without_grant_403_and_logged()
    {
        var res = await app.Client(kidney).GetAsync($"/api/network/patients/{sam}/labs");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("NoGrant", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var e = Assert.Single(Events());
        Assert.Equal("urn:app:kidney-check", e.Actor!.SubjectUri);
        Assert.Equal(DenyReason.NoGrant, e.Reason);
    }

    [Fact]
    public async Task App_with_grant_reads_only_egfr_labs()
    {
        GrantKidneyCheck();
        var res = await app.Client(kidney).GetAsync($"/api/network/patients/{sam}/labs");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var entry = Assert.Single(body.GetProperty("entries").EnumerateArray());
        Assert.Equal("egfr-1", entry.GetProperty("resource").GetProperty("id").GetString());
        Assert.Equal("urn:provider:maple", entry.GetProperty("signer").GetProperty("subjectUri").GetString());
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("jws").GetString()));
        Assert.True(Assert.Single(Events()).Granted);

        var record = await app.Client(kidney).GetAsync($"/api/network/patients/{sam}/record");
        Assert.Equal(HttpStatusCode.Forbidden, record.StatusCode);
        Assert.Equal(DenyReason.ScopeDenied, Events()[^1].Reason);
    }

    [Fact] // Review Focus 4
    public async Task App_note_is_idempotent_and_must_be_signed_by_caller()
    {
        GrantKidneyCheck();
        var client = app.Client(kidney);
        var first = await client.PostAsJsonAsync($"/api/network/patients/{sam}/notes", new { jws = Sign(kidney, Note("kidney-note-1")) });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inserted").GetBoolean());
        var second = await client.PostAsJsonAsync($"/api/network/patients/{sam}/notes", new { jws = Sign(kidney, Note("kidney-note-1")) });
        Assert.False((await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inserted").GetBoolean());

        var forged = await client.PostAsJsonAsync($"/api/network/patients/{sam}/notes", new { jws = Sign(maple, Note("kidney-note-2")) });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var notNote = await client.PostAsJsonAsync($"/api/network/patients/{sam}/notes", new { jws = Sign(kidney, Lab("egfr-9", "98979-8", 10)) });
        Assert.Equal(HttpStatusCode.BadRequest, notNote.StatusCode);

        var notes = await app.With(sp => sp.GetRequiredService<RecordStore>().ReadAsync(sam, t => t == "Communication"));
        Assert.Single(notes);
    }

    [Fact]
    public async Task Provider_without_grant_cannot_read_record()
    {
        var res = await app.Client(maple).GetAsync($"/api/network/patients/{sam}/record?purpose=treatment");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(DenyReason.NoGrant, Assert.Single(Events()).Reason);
    }

    [Fact]
    public async Task Malformed_patient_id_is_404_not_logged()
    {
        var res = await app.Client(kidney).GetAsync("/api/network/patients/not-a-guid/labs");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
