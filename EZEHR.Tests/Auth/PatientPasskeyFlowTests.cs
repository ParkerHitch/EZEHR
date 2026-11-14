using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Api;
using EZEHR.Tests.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Auth;

/// <summary>The passkey endpoints, the session-held challenge and the patient cookie, through the real pipeline.</summary>
public class PatientPasskeyFlowTests : IAsyncLifetime
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

    private HttpClient Browser()
    {
        return _app.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );
    }

    private async Task<string> InviteAsync()
    {
        Party lakeside = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("lakeside"))!;
        return await _app.WithAsync(async services =>
        {
            CreateEnrollmentInviteResult invite = await services
                .GetRequiredService<InviteManager>()
                .CreateEnrollmentInviteAsync(new VerifiedCaller(lakeside, null), "LUC-20417");
            return invite.InviteCode!;
        });
    }

    private static readonly object EmptyAttestation = new
    {
        id = "AQID",
        rawId = "AQID",
        type = "public-key",
        response = new
        {
            attestationObject = "AQID",
            clientDataJSON = "AQID",
            transports = Array.Empty<string>(),
        },
        clientExtensionResults = new { },
    };

    private async Task<RegisteredPasskey> EnrollAsync(HttpClient browser, string inviteCode)
    {
        HttpResponseMessage optionsResponse = await browser.PostAsJsonAsync(
            "/api/patient/passkey/create-registration-options",
            new { inviteCode, displayName = "Sam Taylor" }
        );
        Assert.Equal(HttpStatusCode.OK, optionsResponse.StatusCode);
        JsonElement options = await optionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "required",
            options.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString()
        );
        Assert.Equal(
            "required",
            options
                .GetProperty("authenticatorSelection")
                .GetProperty("userVerification")
                .GetString()
        );
        Assert.Equal("ezehr.test", options.GetProperty("rp").GetProperty("id").GetString());

        RegisteredPasskey passkey = FakeFido2Service.NewPasskey(
            Base64UrlDecode(options.GetProperty("user").GetProperty("id").GetString()!)
        );
        _app.Fido2.NextRegistration = passkey;
        HttpResponseMessage completed = await browser.PostAsJsonAsync(
            "/api/patient/passkey/complete-registration",
            new { attestation = EmptyAttestation, kidneyCheckOptIn = true }
        );
        Assert.True(
            completed.StatusCode == HttpStatusCode.OK,
            await completed.Content.ReadAsStringAsync()
        );
        return passkey;
    }

    [Fact]
    public async Task Enrollment_signs_the_patient_in()
    {
        HttpClient browser = Browser();

        await EnrollAsync(browser, await InviteAsync());

        HttpResponseMessage home = await browser.GetAsync("/Patient/Home");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Sam Taylor", await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patient_pages_redirect_to_sign_in_without_a_cookie()
    {
        HttpResponseMessage home = await Browser().GetAsync("/Patient/Home");

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.StartsWith("/Patient/SignIn", home.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Completion_without_a_challenge_is_rejected()
    {
        _app.Fido2.NextRegistration = FakeFido2Service.NewPasskey([1]);

        HttpResponseMessage completed = await Browser()
            .PostAsJsonAsync(
                "/api/patient/passkey/complete-registration",
                new { attestation = EmptyAttestation, kidneyCheckOptIn = false }
            );

        Assert.Equal(HttpStatusCode.BadRequest, completed.StatusCode);
    }

    [Fact]
    public async Task A_challenge_answers_once()
    {
        HttpClient browser = Browser();
        await EnrollAsync(browser, await InviteAsync());

        HttpResponseMessage replay = await browser.PostAsJsonAsync(
            "/api/patient/passkey/complete-registration",
            new { attestation = EmptyAttestation, kidneyCheckOptIn = false }
        );

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Sign_in_on_a_new_browser()
    {
        RegisteredPasskey passkey = await EnrollAsync(Browser(), await InviteAsync());
        HttpClient browser = Browser();

        HttpResponseMessage options = await browser.PostAsync(
            "/api/patient/passkey/create-sign-in-options",
            null
        );
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        _app.Fido2.NextSignInCounter = 1;
        string credentialId = Convert
            .ToBase64String(passkey.CredentialId)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        HttpResponseMessage signedIn = await browser.PostAsJsonAsync(
            "/api/patient/passkey/complete-sign-in",
            new
            {
                assertion = new
                {
                    id = credentialId,
                    rawId = credentialId,
                    type = "public-key",
                    response = new
                    {
                        authenticatorData = "AQID",
                        clientDataJSON = "AQID",
                        signature = "AQID",
                        userHandle = "AQID",
                    },
                    clientExtensionResults = new { },
                },
            }
        );

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Patient/Home")).StatusCode);
    }

    [Fact]
    public async Task Enroll_page_names_the_inviting_provider()
    {
        string inviteCode = await InviteAsync();

        string page = await Browser().GetStringAsync($"/Patient/Enroll?invite={inviteCode}");

        Assert.Contains("Lakeside Urgent Care invited you", page);
        Assert.Contains(Copy.KidneyOptIn, page);
    }

    private static byte[] Base64UrlDecode(string text)
    {
        string base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(
            base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')
        );
    }
}
