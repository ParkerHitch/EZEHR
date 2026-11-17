using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Auth;
using EZEHR.Tests.Data;

namespace EZEHR.Tests.Api;

/// <summary>A provider's system asks for a launch over the network door, with its certificate.</summary>
public class ClinicianLaunchApiTests : IAsyncLifetime
{
    private EzehrFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        await _app.Database.SetEntraTenantAsync(
            OrgDirectory.ProviderUri("riverside"),
            Guid.Parse(TestClinicianAuthHandler.RiversideTenant)
        );
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private Task<HttpResponseMessage> CreateAsync(X509Certificate2? certificate)
    {
        return _app.Client(certificate)
            .PostAsJsonAsync(
                "/api/network/create-clinician-launch",
                new CreateClinicianLaunchRequest(
                    Guid.Parse(TestClinicianAuthHandler.RiveraObjectId),
                    "RPC-1107",
                    "ENC-2026-11-14-01"
                )
            );
    }

    [Fact]
    public async Task Provider_with_a_tenant_gets_a_launch_link()
    {
        X509Certificate2 riverside = _app.Pki.Issue(OrgDirectory.ProviderCommonName("riverside"));

        HttpResponseMessage response = await CreateAsync(riverside);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreateClinicianLaunchResponse launch = (
            await response.Content.ReadFromJsonAsync<CreateClinicianLaunchResponse>()
        )!;
        Assert.Equal(
            $"https://ezehr.test/Clinician/Launch?launch={launch.LaunchCode}",
            launch.LaunchUrl
        );
    }

    [Fact]
    public async Task Only_a_provider_that_has_clinicians_may_ask()
    {
        X509Certificate2 lakeside = _app.Pki.Issue(OrgDirectory.ProviderCommonName("lakeside"));
        X509Certificate2 kidneyCheck = _app.Pki.Issue(OrgDirectory.KidneyCheckCommonName);

        HttpResponseMessage noCertificate = await CreateAsync(null);
        HttpResponseMessage app = await CreateAsync(kidneyCheck);
        HttpResponseMessage noTenant = await CreateAsync(lakeside);

        Assert.Equal(HttpStatusCode.Unauthorized, noCertificate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, app.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, noTenant.StatusCode);
        JsonElement body = await noTenant.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ProviderHasNoClinicians", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_request_that_names_no_clinician_is_a_bad_request()
    {
        X509Certificate2 riverside = _app.Pki.Issue(OrgDirectory.ProviderCommonName("riverside"));

        HttpResponseMessage response = await _app.Client(riverside)
            .PostAsJsonAsync(
                "/api/network/create-clinician-launch",
                new { providerPatientId = "RPC-1107", encounterId = "ENC-1" }
            );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
