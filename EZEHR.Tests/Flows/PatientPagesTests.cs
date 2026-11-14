using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Api;
using EZEHR.Tests.Auth;
using EZEHR.Tests.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Flows;

/// <summary>The patient's side of sharing, through the pages: code, request, approval, access list, activity.</summary>
public class PatientPagesTests : IAsyncLifetime
{
    private static readonly Party Rivera = new(
        PartyKind.Clinician,
        "urn:entra:tenant:rivera",
        "Dr. Rivera",
        "Riverside Primary Care"
    );

    private EzehrFactory _app = null!;
    private HttpClient _phone = null!;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _phone = _app.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );
        await EnrollSamAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private async Task EnrollSamAsync()
    {
        Party lakeside = OrgDirectory.FindBySubjectUri(OrgDirectory.ProviderUri("lakeside"))!;
        string inviteCode = await _app.WithAsync(async services =>
            (
                await services
                    .GetRequiredService<InviteManager>()
                    .CreateEnrollmentInviteAsync(new VerifiedCaller(lakeside, null), "LUC-20417")
            ).InviteCode!
        );
        JsonElement options = await (
            await _phone.PostAsJsonAsync(
                "/api/patient/passkey/create-registration-options",
                new { inviteCode, displayName = "Sam Taylor" }
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        string userId = options.GetProperty("user").GetProperty("id").GetString()!;
        _app.Fido2.NextRegistration = FakeFido2Service.NewPasskey(
            Convert.FromBase64String(
                userId
                    .Replace('-', '+')
                    .Replace('_', '/')
                    .PadRight((userId.Length + 3) / 4 * 4, '=')
            )
        );
        HttpResponseMessage completed = await _phone.PostAsJsonAsync(
            "/api/patient/passkey/complete-registration",
            new
            {
                attestation = new
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
                },
                kidneyCheckOptIn = true,
            }
        );
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    /// <summary>The page as a person reads it: Razor encodes characters like "·".</summary>
    private async Task<string> ReadAsync(string url)
    {
        return WebUtility.HtmlDecode(await _phone.GetStringAsync(url));
    }

    private async Task<HttpResponseMessage> PostFormAsync(
        string pageUrl,
        string postUrl,
        Dictionary<string, string> fields
    )
    {
        string page = await _phone.GetStringAsync(pageUrl);
        Match token = Regex.Match(
            page,
            "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\""
        );
        Assert.True(token.Success, "no antiforgery token on " + pageUrl);
        fields["__RequestVerificationToken"] = token.Groups[1].Value;
        return await _phone.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Share_request_approve_then_access_and_activity_show_it()
    {
        HttpResponseMessage created = await PostFormAsync(
            "/Patient/Share",
            "/Patient/Share?handler=Create",
            new() { ["duration"] = nameof(ShareDuration.OneHour) }
        );
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        string sharePath = created.Headers.Location!.OriginalString;
        string offerId = Regex.Match(sharePath, "offerId=([0-9a-f-]+)").Groups[1].Value;
        string sharePage = await ReadAsync(sharePath);
        string code = Regex
            .Match(sharePage, "class=\"share-code[^\"]*\">([A-Z0-9-]+)<")
            .Groups[1]
            .Value;
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}$", code);
        Assert.Contains("data:image/png;base64,", sharePage);

        GrantResult redeemed = await _app.WithAsync(services =>
            services.GetRequiredService<GrantManager>().RedeemShareCodeAsync(code, Rivera)
        );
        Assert.True(redeemed.Succeeded);

        string status = await _phone.GetStringAsync(
            $"/Patient/Share?handler=Status&offerId={offerId}"
        );
        Assert.Contains("Give Dr. Rivera at Riverside Primary Care access until", status);

        HttpResponseMessage approved = await PostFormAsync(
            sharePath,
            "/Patient/Share?handler=Decide",
            new()
            {
                ["offerId"] = offerId,
                ["grantId"] = redeemed.Grant!.Id.ToString(),
                ["approve"] = "true",
            }
        );
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        Assert.Contains(
            "Dr. Rivera at Riverside Primary Care can see your record until",
            await ReadAsync(sharePath)
        );

        string access = await ReadAsync("/Patient/Grants");
        Assert.Contains("Dr. Rivera, Riverside Primary Care · until", access);
        Assert.Contains("Kidney Check · lab results only · you allowed this", access);

        string activity = await ReadAsync("/Patient/Activity?handler=List");
        Assert.Contains("Dr. Rivera, Riverside Primary Care, asked to see your record", activity);
    }

    [Fact]
    public async Task Kidney_check_can_be_turned_back_on_after_revoke()
    {
        string kidneyGrantId = Regex
            .Match(await ReadAsync("/Patient/Grants"), "name=\"grantId\" value=\"([0-9a-f-]+)\"")
            .Groups[1]
            .Value;
        Assert.DoesNotContain(Copy.TurnOnKidneyCheck, await ReadAsync("/Patient/Grants"));
        await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=Revoke",
            new() { ["grantId"] = kidneyGrantId }
        );
        Assert.Contains(Copy.TurnOnKidneyCheck, await ReadAsync("/Patient/Grants"));

        HttpResponseMessage turnedOn = await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=TurnOnKidneyCheck",
            []
        );

        Assert.Equal(HttpStatusCode.Redirect, turnedOn.StatusCode);
        string access = await ReadAsync("/Patient/Grants");
        Assert.Contains("Kidney Check · lab results only · you allowed this", access);
        Assert.DoesNotContain(Copy.TurnOnKidneyCheck, access);
    }

    [Fact]
    public async Task Cookie_for_a_deleted_patient_is_signed_out()
    {
        Assert.Equal(HttpStatusCode.OK, (await _phone.GetAsync("/Patient/Home")).StatusCode);
        await _app.Database.Db.DeleteAllDataAsync();

        HttpResponseMessage home = await _phone.GetAsync("/Patient/Home");

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains("/Patient/SignIn", home.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Revoke_moves_access_to_ended()
    {
        Guid kidneyGrantId = Guid.Parse(
            Regex
                .Match(
                    await ReadAsync("/Patient/Grants"),
                    "name=\"grantId\" value=\"([0-9a-f-]+)\""
                )
                .Groups[1]
                .Value
        );

        HttpResponseMessage revoked = await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=Revoke",
            new() { ["grantId"] = kidneyGrantId.ToString() }
        );

        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        string access = await ReadAsync("/Patient/Grants");
        Assert.Contains("Kidney Check · you revoked access", access);
        Assert.DoesNotContain("btn-outline-danger", access);
    }

    [Fact]
    public async Task Access_lists_connected_clinics_and_disconnect_removes_one()
    {
        string access = await ReadAsync("/Patient/Grants");
        Assert.Contains("Lakeside Urgent Care · can add records · linked ", access);

        HttpResponseMessage disconnected = await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=Disconnect",
            new() { ["providerUri"] = "urn:provider:lakeside" }
        );

        Assert.Equal(HttpStatusCode.Redirect, disconnected.StatusCode);
        string after = await ReadAsync("/Patient/Grants");
        Assert.DoesNotContain("Lakeside Urgent Care · can add records", after);
        Assert.Contains(Copy.NoConnectedProviders, after);
        Assert.Contains("Kidney Check · lab results only · you allowed this", after);
    }

    [Fact]
    public async Task Someone_elses_offer_is_not_found()
    {
        HttpResponseMessage status = await _phone.GetAsync(
            $"/Patient/Share?handler=Status&offerId={Guid.NewGuid()}"
        );

        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }
}
