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

/// <summary>The organization audit endpoints, over the network door with a provider's certificate.</summary>
public class OrganizationAuditApiTests : IAsyncLifetime
{
    private static readonly Party Rivera = new(
        PartyKind.Clinician,
        "urn:entra:tid-r:rivera",
        "Dr. Rivera",
        "Riverside Primary Care",
        "urn:provider:riverside"
    );

    private EzehrFactory _app = null!;
    private X509Certificate2 _riverside = null!;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _riverside = _app.Pki.Issue(OrgDirectory.ProviderCommonName("riverside"));
        await _app.WithAsync(async services =>
        {
            // The audit only logs attempts on real patients.
            Guid samId = await services
                .GetRequiredService<RecordManager>()
                .CreatePatientAsync("Sam Taylor");
            return await services
                .GetRequiredService<AuditManager>()
                .AppendAsync(samId, Rivera, "ReadFullRecord", null, true, null);
        });
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    [Fact]
    public async Task Provider_reads_its_audit()
    {
        HttpResponseMessage response = await _app.Client(_riverside)
            .GetAsync("/api/network/read-organization-audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement events = body.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal(Rivera.SubjectUri, events[0].GetProperty("actorUri").GetString());
        Assert.Equal("Clinician", events[0].GetProperty("actorKind").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextAfterId").ValueKind);
    }

    [Fact]
    public async Task No_certificate_is_401()
    {
        HttpResponseMessage response = await _app.Client()
            .GetAsync("/api/network/read-organization-audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NoCertificate", body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Limit_out_of_range_is_400(int limit)
    {
        HttpResponseMessage response = await _app.Client(_riverside)
            .GetAsync($"/api/network/read-organization-audit?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
