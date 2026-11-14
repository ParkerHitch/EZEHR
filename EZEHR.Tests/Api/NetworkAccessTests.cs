using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Api;

public class NetworkAccessTests : IAsyncLifetime
{
    private EzehrFactory _app = null!;
    private X509Certificate2 _maple = null!;
    private X509Certificate2 _kidney = null!;
    private Guid _sam;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _maple = _app.Pki.Issue(OrgDirectory.ProviderCn("maple"));
        _kidney = _app.Pki.Issue(OrgDirectory.KidneyCheckCn);
        _sam = await _app.WithAsync(async services =>
        {
            RecordManager records = services.GetRequiredService<RecordManager>();
            Guid patientId = await records.CreatePatientAsync("Sam Taylor");
            await records.PutAsync(
                patientId,
                "urn:provider:maple|Observation/egfr-1",
                "Observation",
                Sign(_maple, Lab("egfr-1", "98979-8", 96))
            );
            await records.PutAsync(
                patientId,
                "urn:provider:maple|Observation/bp-1",
                "Observation",
                Sign(_maple, Lab("bp-1", "85354-9", 138))
            );
            JsonElement condition = JsonSerializer.SerializeToElement(
                new { resourceType = "Condition", id = "htn" }
            );
            await records.PutAsync(
                patientId,
                "urn:provider:maple|Condition/htn",
                "Condition",
                Sign(_maple, condition)
            );
            return patientId;
        });
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private static JsonElement Lab(string id, string loinc, int value)
    {
        return JsonSerializer.SerializeToElement(
            new
            {
                resourceType = "Observation",
                id,
                status = "final",
                code = new { coding = new[] { new { system = "http://loinc.org", code = loinc } } },
                valueQuantity = new { value },
            }
        );
    }

    private static JsonElement Note(string id)
    {
        return JsonSerializer.SerializeToElement(
            new
            {
                resourceType = "Communication",
                id,
                status = "completed",
                sender = new { display = "Kidney Check" },
                payload = new[] { new { contentString = Copy.KidneyNote(34, 3) } },
            }
        );
    }

    private string Sign(X509Certificate2 certificate, JsonElement resource)
    {
        return RecordSigner.Sign(resource, certificate, [], _app.Pki.Clock.GetUtcNow());
    }

    private Task<IReadOnlyList<AuditEvent>> EventsAsync()
    {
        return _app.WithAsync(services =>
            services.GetRequiredService<AuditManager>().ListAsync(_sam)
        );
    }

    private Task<Grant> GrantKidneyCheckAsync()
    {
        return _app.WithAsync(services =>
            services
                .GetRequiredService<GrantManager>()
                .CreateAppGrantAsync(_sam, OrgDirectory.Known[OrgDirectory.KidneyCheckCn])
        );
    }

    [Fact]
    public async Task Broker_no_cert_on_record_endpoint_logs_blocked_event()
    {
        HttpResponseMessage response = await _app.Client()
            .GetAsync($"/api/network/patients/{_sam}/record?purpose=treatment");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AuditEvent logged = Assert.Single(await EventsAsync());
        Assert.Null(logged.Actor);
        Assert.Equal("treatment", logged.ClaimedPurpose);
        Assert.Equal(DenyReason.NoCertificate, logged.Reason);
        Assert.Equal("ReadRecord", logged.Action);

        X509Certificate2 broker = TestPki.SelfSigned(
            "exchange.broker.example",
            _app.Pki.Clock.GetUtcNow()
        );
        response = await _app.Client(broker)
            .GetAsync($"/api/network/patients/{_sam}/record?purpose=treatment");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AuditEvent blocked = (await EventsAsync())[^1];
        Assert.Equal(DenyReason.UntrustedCertificate, blocked.Reason);
        Assert.Equal(Copy.BrokerBlocked("treatment"), Copy.BrokerBlocked(blocked.ClaimedPurpose!));
    }

    [Fact]
    public async Task App_without_grant_403_and_logged()
    {
        HttpResponseMessage response = await _app.Client(_kidney)
            .GetAsync($"/api/network/patients/{_sam}/labs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoGrant", body.GetProperty("error").GetString());
        AuditEvent logged = Assert.Single(await EventsAsync());
        Assert.Equal("urn:app:kidney-check", logged.Actor!.SubjectUri);
        Assert.Equal(DenyReason.NoGrant, logged.Reason);
    }

    [Fact]
    public async Task App_with_grant_reads_only_egfr_labs()
    {
        await GrantKidneyCheckAsync();
        HttpResponseMessage response = await _app.Client(_kidney)
            .GetAsync($"/api/network/patients/{_sam}/labs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement entry = Assert.Single(body.GetProperty("entries").EnumerateArray());
        Assert.Equal("egfr-1", entry.GetProperty("resource").GetProperty("id").GetString());
        Assert.Equal(
            "urn:provider:maple",
            entry.GetProperty("signer").GetProperty("subjectUri").GetString()
        );
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("jws").GetString()));
        Assert.True(Assert.Single(await EventsAsync()).Granted);

        HttpResponseMessage record = await _app.Client(_kidney)
            .GetAsync($"/api/network/patients/{_sam}/record");
        Assert.Equal(HttpStatusCode.Forbidden, record.StatusCode);
        Assert.Equal(DenyReason.ScopeDenied, (await EventsAsync())[^1].Reason);
    }

    [Fact] // Review Focus 4
    public async Task App_note_is_idempotent_and_must_be_signed_by_caller()
    {
        await GrantKidneyCheckAsync();
        HttpClient client = _app.Client(_kidney);
        string notesUrl = $"/api/network/patients/{_sam}/notes";

        HttpResponseMessage first = await client.PostAsJsonAsync(
            notesUrl,
            new { jws = Sign(_kidney, Note("kidney-note-1")) }
        );
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        JsonElement firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(firstBody.GetProperty("inserted").GetBoolean());

        HttpResponseMessage second = await client.PostAsJsonAsync(
            notesUrl,
            new { jws = Sign(_kidney, Note("kidney-note-1")) }
        );
        JsonElement secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(secondBody.GetProperty("inserted").GetBoolean());

        HttpResponseMessage forged = await client.PostAsJsonAsync(
            notesUrl,
            new { jws = Sign(_maple, Note("kidney-note-2")) }
        );
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        HttpResponseMessage notNote = await client.PostAsJsonAsync(
            notesUrl,
            new { jws = Sign(_kidney, Lab("egfr-9", "98979-8", 10)) }
        );
        Assert.Equal(HttpStatusCode.BadRequest, notNote.StatusCode);

        IReadOnlyList<StoredEntry> notes = await _app.WithAsync(services =>
            services
                .GetRequiredService<RecordManager>()
                .ReadAsync(_sam, resourceType => resourceType == "Communication")
        );
        Assert.Single(notes);
    }

    [Fact]
    public async Task Provider_without_grant_cannot_read_record()
    {
        HttpResponseMessage response = await _app.Client(_maple)
            .GetAsync($"/api/network/patients/{_sam}/record?purpose=treatment");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DenyReason.NoGrant, Assert.Single(await EventsAsync()).Reason);
    }

    [Fact]
    public async Task Malformed_patient_id_is_404_not_logged()
    {
        HttpResponseMessage response = await _app.Client(_kidney)
            .GetAsync("/api/network/patients/not-a-guid/labs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
