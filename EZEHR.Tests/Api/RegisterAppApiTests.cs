using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Api;

/// <summary>The platform operator adds an app over the network door; nobody else can, and listing it grants nothing.</summary>
public class RegisterAppApiTests : IAsyncLifetime
{
    private EzehrFactory _app = null!;
    private X509Certificate2 _sleepCheck = null!;
    private Guid _sam;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _sleepCheck = _app.Pki.Issue(OrgDirectory.AppCommonName("sleep-check"));
        _sam = await _app.WithAsync(services =>
            services.GetRequiredService<RecordManager>().CreatePatientAsync("Sam Taylor")
        );
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private Task<HttpResponseMessage> RegisterAsync(
        X509Certificate2? certificate,
        GrantScope scope = GrantScope.MedicationsLabsAndNotes
    )
    {
        return _app.Client(certificate)
            .PostAsJsonAsync(
                "/api/network/register-app",
                new RegisterAppRequest(
                    "sleep-check",
                    "Sleep Check",
                    scope,
                    "Looks at your medications for ones that can disturb sleep."
                )
            );
    }

    private Task<HttpResponseMessage> ReadMedicationsAsync()
    {
        return _app.Client(_sleepCheck).GetAsync($"/api/network/patients/{_sam}/read-medications");
    }

    [Fact]
    public async Task Platform_registers_app_which_patients_then_see_off_and_can_turn_on()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReadMedicationsAsync()).StatusCode);

        HttpResponseMessage response = await RegisterAsync(_app.Platform);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        RegisterAppResponse registered = (
            await response.Content.ReadFromJsonAsync<RegisterAppResponse>()
        )!;
        Assert.Equal("urn:app:sleep-check", registered.Uri);
        Assert.Equal("sleep-check.apps.ezehr", registered.CertificateCommonName);
        // Listed, but nobody has turned it on: verified and refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await ReadMedicationsAsync()).StatusCode);
        AccessOverview overview = await _app.WithAsync(services =>
            services.GetRequiredService<GrantManager>().ListAccessAsync(_sam)
        );
        AppLine listed = Assert.Single(overview.Apps, app => app.AppUri == registered.Uri);
        Assert.Null(listed.Grant);
        Assert.Equal(
            "Looks at your medications for ones that can disturb sleep.",
            listed.Description
        );
        Assert.Equal("medications and lab results only", listed.What);

        await _app.WithAsync(services =>
            services.GetRequiredService<GrantManager>().TurnOnAppAsync(_sam, registered.Uri)
        );

        Assert.Equal(HttpStatusCode.OK, (await ReadMedicationsAsync()).StatusCode);
    }

    [Fact]
    public async Task Only_the_platform_certificate_registers_an_app()
    {
        X509Certificate2 maple = _app.Pki.Issue(OrgDirectory.ProviderCommonName("maple"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(maple)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(_sleepCheck)).StatusCode);
    }

    [Fact]
    public async Task An_app_cannot_declare_the_full_scope_and_a_second_registration_conflicts()
    {
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await RegisterAsync(_app.Platform, GrantScope.Full)).StatusCode
        );
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(_app.Platform)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync(_app.Platform)).StatusCode);
    }
}
