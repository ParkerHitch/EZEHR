using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
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
        "Riverside Primary Care",
        "urn:provider:riverside"
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
        Party lakeside = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("lakeside"))!;
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

    private static string TimelinePollPath(string page)
    {
        return Regex
            .Match(page, "hx-get=\"(/Patient/Home\\?[^\"]*handler=Timeline[^\"]*)\"")
            .Groups[1]
            .Value;
    }

    [Fact]
    public async Task Home_timeline_poll_shows_a_pushed_record_without_a_refresh()
    {
        string pollPath = TimelinePollPath(await ReadAsync("/Patient/Home"));
        HttpResponseMessage unchanged = await _phone.GetAsync(pollPath);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);

        Party lakeside = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("lakeside"))!;
        X509Certificate2 certificate = _app.Pki.Issue(OrgDirectory.ProviderCommonName("lakeside"));
        JsonElement condition = JsonSerializer.SerializeToElement(
            new
            {
                resourceType = "Condition",
                id = "poll-test",
                recordedDate = "2026-11-14",
                code = new { text = "Seasonal allergies" },
            }
        );
        PushSignedRecordsResult pushed = await _app.WithAsync(services =>
            services
                .GetRequiredService<RecordManager>()
                .PushSignedRecordsAsync(
                    new VerifiedCaller(lakeside, null),
                    "LUC-20417",
                    [RecordSigner.Sign(condition, certificate, [], _app.Pki.Clock.GetUtcNow())]
                )
        );
        Assert.True(pushed.Allowed && pushed.Error is null, $"{pushed.Error}");

        HttpResponseMessage changed = await _phone.GetAsync(pollPath);
        string timeline = WebUtility.HtmlDecode(await changed.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Contains("Seasonal allergies", timeline);
        string nextPollPath = TimelinePollPath(timeline);
        Assert.NotEqual(pollPath, nextPollPath);
        Assert.Equal(HttpStatusCode.NoContent, (await _phone.GetAsync(nextPollPath)).StatusCode);
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
    public async Task Apps_section_turns_kidney_check_off_and_back_on()
    {
        string on = await ReadAsync("/Patient/Grants");
        string kidneyGrantId = Regex
            .Match(on, "name=\"grantId\" value=\"([0-9a-f-]+)\"")
            .Groups[1]
            .Value;
        Assert.Contains(Copy.TurnOffKidneyCheck, on);
        Assert.Contains(">On<", on);
        Assert.Contains("No clinicians.", on);
        Assert.DoesNotContain(Copy.TurnOnKidneyCheck, on);

        await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=Revoke",
            new() { ["grantId"] = kidneyGrantId }
        );
        string off = await ReadAsync("/Patient/Grants");
        Assert.Contains(Copy.TurnOnKidneyCheck, off);
        Assert.Contains(Copy.KidneyOptIn, off);
        Assert.Contains(">Off<", off);

        HttpResponseMessage turnedOn = await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=TurnOnKidneyCheck",
            []
        );

        Assert.Equal(HttpStatusCode.Redirect, turnedOn.StatusCode);
        string access = await ReadAsync("/Patient/Grants");
        Assert.Contains("Kidney Check · lab results only · you allowed this", access);
        Assert.Contains(Copy.TurnOffKidneyCheck, access);
        Assert.DoesNotContain(Copy.TurnOnKidneyCheck, access);
    }

    [Fact]
    public async Task Emergency_access_section_turns_on_shows_the_code_and_turns_off()
    {
        string off = await ReadAsync("/Patient/Grants");
        Assert.Contains(Copy.EmergencyAccessOffHint, off);
        Assert.Contains(Copy.TurnOnEmergencyAccess, off);

        HttpResponseMessage turnedOn = await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=TurnOnEmergencyAccess",
            []
        );

        Assert.Equal(HttpStatusCode.Redirect, turnedOn.StatusCode);
        string on = await ReadAsync("/Patient/Grants");
        Match code = Regex.Match(on, "font-monospace\">([A-Z0-9]{4}-[A-Z0-9]{4})<");
        Assert.True(code.Success, "no emergency code on the Access page");
        Assert.Contains(Copy.EmergencyAccessOnHint, on);
        string homeBefore = await ReadAsync("/Patient/Home");
        Assert.DoesNotContain("Emergency access:", homeBefore);
        GrantResult opened = await _app.WithAsync(services =>
            services
                .GetRequiredService<GrantManager>()
                .OpenEmergencyAccessAsync(code.Groups[1].Value, "Unresponsive", Rivera)
        );
        Assert.True(opened.Succeeded);
        string access = await ReadAsync("/Patient/Grants");
        Assert.Contains("Emergency access: Dr. Rivera, Riverside Primary Care · until", access);
        string activity = await ReadAsync("/Patient/Activity");
        Assert.Contains(
            "opened your record without your approval. Reason given: Unresponsive",
            activity
        );
        Assert.Contains("list-group-item-warning", activity);
        // The open home page learns about it on its next poll, and loses the alert once it's revoked.
        HttpResponseMessage homePoll = await _phone.GetAsync(TimelinePollPath(homeBefore));
        string homeAlert = WebUtility.HtmlDecode(await homePoll.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, homePoll.StatusCode);
        Assert.Contains("Emergency access: Dr. Rivera, Riverside Primary Care · until", homeAlert);
        Assert.Contains("reason given: Unresponsive", homeAlert);
        Assert.Contains(Copy.EmergencyAccessReview, homeAlert);
        await _app.WithAsync(services =>
            services
                .GetRequiredService<GrantManager>()
                .RevokeAsync(opened.Grant!.PatientId, opened.Grant.Id)
        );
        HttpResponseMessage afterRevoke = await _phone.GetAsync(TimelinePollPath(homeAlert));
        Assert.Equal(HttpStatusCode.OK, afterRevoke.StatusCode);
        Assert.DoesNotContain(
            "Emergency access:",
            WebUtility.HtmlDecode(await afterRevoke.Content.ReadAsStringAsync())
        );

        await PostFormAsync(
            "/Patient/Grants",
            "/Patient/Grants?handler=TurnOffEmergencyAccess",
            []
        );

        string offAgain = await ReadAsync("/Patient/Grants");
        Assert.Contains(Copy.TurnOnEmergencyAccess, offAgain);
        Assert.DoesNotContain(code.Groups[1].Value, offAgain);
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
