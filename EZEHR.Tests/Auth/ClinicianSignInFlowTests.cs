using System.Net;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Tests.Api;
using EZEHR.Tests.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EZEHR.Tests.Auth;

/// <summary>The clinician cookie, the sign-in page and the optional Entra wiring, through the real pipeline.</summary>
public class ClinicianSignInFlowTests : IAsyncLifetime
{
    private EzehrFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private static HttpClient Browser(WebApplicationFactory<Program> app, string? clinician = null)
    {
        HttpClient client = app.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );
        if (clinician is not null)
        {
            client.DefaultRequestHeaders.Add(TestClinicianAuthHandler.Header, clinician);
        }
        return client;
    }

    private static async Task<string> PageTextAsync(HttpClient browser, string path)
    {
        HttpResponseMessage response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Index_without_Entra_config_says_sign_in_is_not_set_up()
    {
        string page = await PageTextAsync(Browser(_app), "/Clinician");

        Assert.Contains(Copy.ClinicianSignInUnavailable, page);
    }

    [Fact]
    public async Task Index_shows_who_is_signed_in_until_the_shift_ends()
    {
        WebApplicationFactory<Program> app = await TestClinicianAuthHandler.InstallAsync(_app);
        string clinician = TestClinicianAuthHandler.HeaderValue(
            CallerManager.ClinicianRole,
            TestPki.Start
        );

        string page = await PageTextAsync(Browser(app, clinician), "/Clinician");

        // 20:00 UTC is 3:00 PM in Indianapolis; the shift ends 10 hours later.
        Assert.Contains("Dr. Rivera · Riverside Primary Care · signed in until 1:00 AM", page);
    }

    [Fact]
    public async Task Index_shows_NotAClinician_for_an_account_without_the_role()
    {
        WebApplicationFactory<Program> app = await TestClinicianAuthHandler.InstallAsync(_app);
        string reader = TestClinicianAuthHandler.HeaderValue("Reader", TestPki.Start);

        string page = await PageTextAsync(Browser(app, reader), "/Clinician");

        Assert.Contains(Copy.NotAClinician, page);
        Assert.DoesNotContain("signed in until", page);
    }

    [Fact]
    public async Task Entra_sign_in_is_wired_when_configured()
    {
        WebApplicationFactory<Program> app = _app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
            builder.UseSetting("AzureAd:TenantId", TestClinicianAuthHandler.RiversideTenant);
            builder.UseSetting("AzureAd:ClientId", "5f0c2b1a-7d3e-4c9b-8a6f-2e1d0c9b8a7f");
        });

        IAuthenticationSchemeProvider schemes =
            app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        OpenIdConnectOptions oidc = app
            .Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CallerManager.ClinicianOidcScheme);
        CookieAuthenticationOptions cookie = app
            .Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CallerManager.ClinicianScheme);

        Assert.NotNull(await schemes.GetSchemeAsync(CallerManager.ClinicianOidcScheme));
        Assert.Equal(CallerManager.ClinicianScheme, oidc.SignInScheme);
        Assert.False(oidc.UseTokenLifetime);
        Assert.Equal("id_token", oidc.ResponseType);
        Assert.Equal(TimeSpan.FromHours(10), cookie.ExpireTimeSpan);
        Assert.False(cookie.SlidingExpiration);
        string page = await PageTextAsync(Browser(app), "/Clinician");
        Assert.DoesNotContain(Copy.ClinicianSignInUnavailable, page);
        Assert.Contains("handler=SignIn", page);
    }
}
