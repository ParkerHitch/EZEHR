using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Api;
using EZEHR.Tests.Auth;
using EZEHR.Tests.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Flows;

/// <summary>
/// The share flow end to end: the patient's side goes through GrantManager, the clinician's through the real
/// pages, with TestClinicianAuthHandler standing in for the Entra-issued clinician cookie.
/// </summary>
public class ShareFlowTests : IAsyncLifetime
{
    private static readonly string KidneyNoteText = Copy.KidneyNote(31, 3);

    private EzehrFactory _app = null!;
    private WebApplicationFactory<Program> _clinicianApp = null!;
    private Guid _sam;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _clinicianApp = await TestClinicianAuthHandler.InstallAsync(_app);
        _sam = await WithAsync(services =>
            services.GetRequiredService<RecordManager>().CreatePatientAsync("Sam Taylor")
        );
        await SeedRecordAsync();
    }

    public async Task DisposeAsync()
    {
        await _clinicianApp.DisposeAsync();
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = _clinicianApp.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Every provider's seed visits, signed and pushed as each provider, then the Kidney Check note.</summary>
    private async Task SeedRecordAsync()
    {
        string seedDirectory = SeedDirectory();
        foreach (string path in Directory.GetFiles(seedDirectory, "*.json"))
        {
            using JsonDocument seed = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            string slug = seed.RootElement.GetProperty("provider").GetString()!;
            string providerPatientId = seed
                .RootElement.GetProperty("providerPatientId")
                .GetString()!;
            await using (SqlConnection connection = await _app.Database.Db.OpenAsync())
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id)
                    VALUES (@patientId, @providerUri, @providerPatientId)
                    """,
                    new
                    {
                        patientId = _sam,
                        providerUri = OrgDirectory.ProviderUri(slug),
                        providerPatientId,
                    }
                );
            }
            X509Certificate2 certificate = _app.Pki.Issue(OrgDirectory.ProviderCommonName(slug));
            List<string> signedEntries = seed
                .RootElement.GetProperty("visits")
                .EnumerateArray()
                .SelectMany(visit => visit.GetProperty("resources").EnumerateArray())
                .Select(resource =>
                    RecordSigner.Sign(resource, certificate, [], _app.Pki.Clock.GetUtcNow())
                )
                .ToList();
            Party provider = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri(slug))!;
            PushSignedRecordsResult pushed = await WithAsync(services =>
                services
                    .GetRequiredService<RecordManager>()
                    .PushSignedRecordsAsync(
                        new VerifiedCaller(provider, null),
                        providerPatientId,
                        signedEntries
                    )
            );
            Assert.True(pushed.Allowed && pushed.Error is null, $"{slug}: {pushed.Error}");
        }

        Party kidneyCheck = OrgDirectory.Demo.FindByUri("urn:app:kidney-check")!;
        X509Certificate2 appCertificate = _app.Pki.Issue(OrgDirectory.KidneyCheckCommonName);
        JsonElement note = JsonSerializer.SerializeToElement(
            new
            {
                resourceType = "Communication",
                id = "kidney-check",
                status = "completed",
                sent = "2026-11-13",
                sender = new { display = "Kidney Check" },
                payload = new[] { new { contentString = KidneyNoteText } },
            }
        );
        WriteSignedNoteResult written = await WithAsync(async services =>
        {
            await services
                .GetRequiredService<GrantManager>()
                .CreateAppGrantAsync(_sam, kidneyCheck);
            return await services
                .GetRequiredService<RecordManager>()
                .WriteSignedNoteAsync(
                    _sam,
                    new VerifiedCaller(kidneyCheck, null),
                    RecordSigner.Sign(note, appCertificate, [], _app.Pki.Clock.GetUtcNow())
                );
        });
        Assert.True(written.Stored);
    }

    private static string SeedDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "EZEHR.Tools", "seed");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("EZEHR.Tools/seed");
    }

    private HttpClient ClinicianBrowser()
    {
        HttpClient client = _clinicianApp.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );
        client.DefaultRequestHeaders.Add(
            TestClinicianAuthHandler.Header,
            TestClinicianAuthHandler.HeaderValue(
                CallerManager.ClinicianRole,
                _app.Pki.Clock.GetUtcNow()
            )
        );
        return client;
    }

    /// <summary>
    /// Riverside's system makes a launch for Dr. Rivera from the chart with this patient id, and Dr. Rivera's
    /// browser opens the link. Returns where the launch page sends the browser: the share-code form.
    /// </summary>
    private async Task<string> LaunchAsync(
        HttpClient browser,
        string providerPatientId = "RPC-1107"
    )
    {
        Party riverside = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("riverside"))!;
        CreateClinicianLaunchResult launch = await WithAsync(services =>
            services
                .GetRequiredService<LaunchManager>()
                .CreateClinicianLaunchAsync(
                    new VerifiedCaller(riverside, null),
                    Guid.Parse(TestClinicianAuthHandler.RiveraObjectId),
                    providerPatientId,
                    "ENC-2026-11-14-01"
                )
        );
        HttpResponseMessage opened = await browser.GetAsync(
            new Uri(launch.LaunchUrl!).PathAndQuery
        );
        Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
        string redeemPath = opened.Headers.Location!.OriginalString;
        Assert.StartsWith("/Clinician/Redeem?launchId=", redeemPath);
        return redeemPath;
    }

    /// <summary>
    /// Posts a clinician form, antiforgery token included. A failed attempt redirects back to the form with the
    /// error, so this follows that one redirect and returns the form as the clinician then sees it.
    /// </summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient browser,
        string formPath,
        Dictionary<string, string> fields
    )
    {
        string form = await browser.GetStringAsync(formPath);
        fields["__RequestVerificationToken"] = Regex
            .Match(form, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")
            .Groups[1]
            .Value;
        HttpResponseMessage response = await browser.PostAsync(
            formPath,
            new FormUrlEncodedContent(fields)
        );
        string? location = response.Headers.Location?.OriginalString;
        if (location is not null && location.Contains("error=", StringComparison.Ordinal))
        {
            return await browser.GetAsync(location);
        }
        return response;
    }

    /// <summary>Opens a launch from Sam's chart, then fills in and submits the share-code form.</summary>
    private async Task<HttpResponseMessage> RedeemAsync(HttpClient browser, string code)
    {
        return await PostFormAsync(browser, await LaunchAsync(browser), new() { ["Code"] = code });
    }

    private async Task<ShareOffer> CreateOfferAsync()
    {
        return await WithAsync(services =>
            services
                .GetRequiredService<GrantManager>()
                .CreateShareOfferAsync(_sam, ShareDuration.FiveMinutes)
        );
    }

    private async Task<Grant> ClinicianGrantAsync()
    {
        IReadOnlyList<Grant> grants = await WithAsync(services =>
            services.GetRequiredService<GrantManager>().ListForPatientAsync(_sam)
        );
        return grants.Single(grant => grant.Grantee.Kind == PartyKind.Clinician);
    }

    private async Task<IReadOnlyList<AuditEvent>> ActivityAsync()
    {
        return await WithAsync(services =>
            services.GetRequiredService<AuditManager>().ListForPatientAsync(_sam)
        );
    }

    private static int CountOccurrences(string text, string value)
    {
        return Regex.Matches(text, Regex.Escape(value), RegexOptions.IgnoreCase).Count;
    }

    private string RecordPath()
    {
        return $"/Clinician/Record?patientId={_sam}";
    }

    /// <summary>
    /// Opens a launch from a chart EZEHR can't match to anyone, as in an emergency room, then fills in and submits
    /// the emergency form reached from the share-code form.
    /// </summary>
    private async Task<HttpResponseMessage> OpenEmergencyAccessAsync(
        HttpClient browser,
        string code,
        string reason
    )
    {
        string redeemPath = await LaunchAsync(browser, "RPC-ER-0042");
        return await PostFormAsync(
            browser,
            redeemPath.Replace("/Clinician/Redeem", "/Clinician/Emergency"),
            new() { ["Code"] = code, ["Reason"] = reason }
        );
    }

    [Fact]
    public async Task Emergency_access_opens_the_record_without_approval_and_can_be_revoked()
    {
        HttpClient browser = ClinicianBrowser();
        string code = await WithAsync(services =>
            services.GetRequiredService<GrantManager>().TurnOnEmergencyAccessAsync(_sam)
        );

        HttpResponseMessage wrong = await OpenEmergencyAccessAsync(
            browser,
            "ZZZZ-ZZZZ",
            "Unresponsive after a car crash"
        );
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains(
            Copy.EmergencyCodeUnknown,
            WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync())
        );
        HttpResponseMessage noCode = await OpenEmergencyAccessAsync(browser, "", "Unresponsive");
        Assert.Equal(HttpStatusCode.OK, noCode.StatusCode);
        Assert.Contains(
            Copy.EmergencyCodeUnknown,
            WebUtility.HtmlDecode(await noCode.Content.ReadAsStringAsync())
        );
        HttpResponseMessage noReason = await OpenEmergencyAccessAsync(browser, code, " ");
        Assert.Contains(
            Copy.EmergencyReasonMissing,
            WebUtility.HtmlDecode(await noReason.Content.ReadAsStringAsync())
        );

        HttpResponseMessage opened = await OpenEmergencyAccessAsync(
            browser,
            GrantManager.FormatCode(code),
            "Unresponsive after a car crash"
        );

        Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
        Assert.Equal(RecordPath(), opened.Headers.Location!.OriginalString);
        string record = WebUtility.HtmlDecode(await browser.GetStringAsync(RecordPath()));
        Assert.Contains("Sam Taylor · emergency access until", record);
        Assert.Contains(KidneyNoteText, record);
        IReadOnlyList<AuditEvent> activity = await ActivityAsync();
        Assert.Equal(GrantManager.OpenEmergencyAccessAction, activity[^2].Action);
        Assert.Equal("Unresponsive after a car crash", activity[^2].ClaimedPurpose);
        Assert.Equal(GrantManager.EmergencyPurpose, activity[^1].ClaimedPurpose);

        Grant grant = await ClinicianGrantAsync();
        await WithAsync(services =>
            services.GetRequiredService<GrantManager>().RevokeAsync(_sam, grant.Id)
        );
        HttpResponseMessage poll = await browser.GetAsync(
            $"/Clinician/Record?handler=Status&patientId={_sam}"
        );
        Assert.Equal("/Clinician/AccessEnded", poll.Headers.GetValues("HX-Redirect").Single());
    }

    [Fact]
    public async Task Without_a_launch_there_is_no_share_code_form()
    {
        HttpClient browser = ClinicianBrowser();

        HttpResponseMessage redeem = await browser.GetAsync("/Clinician/Redeem");
        HttpResponseMessage emergency = await browser.GetAsync(
            $"/Clinician/Emergency?launchId={Guid.NewGuid()}"
        );
        string index = WebUtility.HtmlDecode(await browser.GetStringAsync("/Clinician/Index"));

        Assert.Equal("/Clinician", redeem.Headers.Location!.OriginalString);
        Assert.Equal("/Clinician", emergency.Headers.Location!.OriginalString);
        Assert.Contains(Copy.OpenFromChart, index);
        Assert.DoesNotContain("Enter a share code", index);
    }

    [Fact]
    public async Task Launch_link_opens_the_share_code_form_for_the_clinician_it_names()
    {
        HttpClient browser = ClinicianBrowser();

        string form = WebUtility.HtmlDecode(
            await browser.GetStringAsync(await LaunchAsync(browser))
        );

        Assert.Contains("Opened from chart RPC-1107 · visit ENC-2026-11-14-01", form);
    }

    [Fact]
    public async Task Launch_link_made_for_another_clinician_or_opened_signed_out_goes_nowhere()
    {
        Party riverside = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("riverside"))!;
        CreateClinicianLaunchResult forSomeoneElse = await WithAsync(services =>
            services
                .GetRequiredService<LaunchManager>()
                .CreateClinicianLaunchAsync(
                    new VerifiedCaller(riverside, null),
                    Guid.NewGuid(),
                    "RPC-1107",
                    "ENC-2026-11-14-01"
                )
        );
        string launchPath = new Uri(forSomeoneElse.LaunchUrl!).PathAndQuery;
        HttpClient signedOut = _clinicianApp.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );

        HttpResponseMessage wrongClinician = await ClinicianBrowser().GetAsync(launchPath);
        HttpResponseMessage notSignedIn = await signedOut.GetAsync(launchPath);

        Assert.Equal(HttpStatusCode.OK, wrongClinician.StatusCode);
        Assert.Contains(
            Copy.LaunchForAnotherClinician,
            WebUtility.HtmlDecode(await wrongClinician.Content.ReadAsStringAsync())
        );
        // Not signed in: sent to sign in, never to the form.
        Assert.Equal(HttpStatusCode.Redirect, notSignedIn.StatusCode);
        Assert.DoesNotContain("/Clinician/Redeem", notSignedIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Share_code_from_the_wrong_chart_is_refused_and_still_works_from_the_right_one()
    {
        HttpClient browser = ClinicianBrowser();
        ShareOffer offer = await CreateOfferAsync();

        HttpResponseMessage wrongChart = await PostFormAsync(
            browser,
            await LaunchAsync(browser, "RPC-9999"),
            new() { ["Code"] = offer.Code }
        );
        HttpResponseMessage rightChart = await RedeemAsync(browser, offer.Code);

        Assert.Equal(HttpStatusCode.OK, wrongChart.StatusCode);
        Assert.Contains(
            Copy.ShareCodeNotForLaunchedPatient,
            WebUtility.HtmlDecode(await wrongChart.Content.ReadAsStringAsync())
        );
        Assert.Equal(
            $"/Clinician/Wait?patientId={_sam}",
            rightChart.Headers.Location!.OriginalString
        );
        IReadOnlyList<AuditEvent> activity = await ActivityAsync();
        Assert.Equal(DenyReason.LaunchPatientMismatch, activity[^2].Reason);
        Assert.True(activity[^1].Granted);
    }

    [Fact]
    public async Task Empty_share_code_shows_the_unknown_code_message()
    {
        HttpResponseMessage redeemed = await RedeemAsync(ClinicianBrowser(), "");

        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.Contains(
            Copy.ShareCodeUnknown,
            WebUtility.HtmlDecode(await redeemed.Content.ReadAsStringAsync())
        );
    }

    [Fact]
    public async Task Denied_request_ends_without_a_blocked_read()
    {
        HttpClient browser = ClinicianBrowser();
        ShareOffer offer = await CreateOfferAsync();
        await RedeemAsync(browser, GrantManager.FormatCode(offer.Code));
        Grant grant = await ClinicianGrantAsync();
        await WithAsync(services =>
            services.GetRequiredService<GrantManager>().ApproveOrDenyAsync(_sam, grant.Id, false)
        );

        HttpResponseMessage poll = await browser.GetAsync(
            $"/Clinician/Wait?handler=Status&patientId={_sam}"
        );

        Assert.Equal("/Clinician/AccessEnded", poll.Headers.GetValues("HX-Redirect").Single());
        Assert.DoesNotContain(await ActivityAsync(), audit => !audit.Granted);
    }

    [Fact]
    public async Task Full_share_flow()
    {
        HttpClient browser = ClinicianBrowser();

        // 1-2. The patient makes a code; the clinician redeems it and waits.
        ShareOffer offer = await CreateOfferAsync();
        HttpResponseMessage redeemed = await RedeemAsync(
            browser,
            GrantManager.FormatCode(offer.Code)
        );
        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
        Assert.Equal(
            $"/Clinician/Wait?patientId={_sam}",
            redeemed.Headers.Location!.OriginalString
        );
        Assert.Equal(GrantStatus.Pending, (await ClinicianGrantAsync()).Status);

        // 3. Until the patient approves, the record sends the clinician back to Wait.
        HttpResponseMessage pending = await browser.GetAsync(RecordPath());
        Assert.Equal(HttpStatusCode.Redirect, pending.StatusCode);
        Assert.Equal($"/Clinician/Wait?patientId={_sam}", pending.Headers.Location!.OriginalString);
        string waiting = WebUtility.HtmlDecode(
            await browser.GetStringAsync($"/Clinician/Wait?patientId={_sam}")
        );
        Assert.Contains(Copy.WaitingForApproval, waiting);
        Assert.Contains("every 2s", waiting);

        // 4. The patient approves; the next poll sends the clinician to the record.
        Grant grant = await ClinicianGrantAsync();
        GrantResult approved = await WithAsync(services =>
            services.GetRequiredService<GrantManager>().ApproveOrDenyAsync(_sam, grant.Id, true)
        );
        Assert.True(approved.Succeeded);
        HttpResponseMessage poll = await browser.GetAsync(
            $"/Clinician/Wait?handler=Status&patientId={_sam}"
        );
        Assert.Equal(RecordPath(), poll.Headers.GetValues("HX-Redirect").Single());

        // 5. The record: every entry with its signer badge, and the Kidney Check note.
        HttpResponseMessage record = await browser.GetAsync(RecordPath());
        Assert.Equal(HttpStatusCode.OK, record.StatusCode);
        string recordPage = WebUtility.HtmlDecode(await record.Content.ReadAsStringAsync());
        Assert.True(CountOccurrences(recordPage, "signed by ") >= 13, recordPage);
        Assert.Contains(KidneyNoteText, recordPage);
        // 20:00 UTC is 3:00 PM in Indianapolis; a 5-minute grant ends at 3:05 PM.
        Assert.Contains(Copy.ClinicianRecordHeader("Sam Taylor", "3:05 PM"), recordPage);
        Assert.DoesNotContain("<textarea", recordPage);

        // 6. Sam's activity shows Dr. Rivera's read.
        Assert.Contains(await ActivityAsync(), audit =>
            audit.Action == nameof(AccessAction.ReadFullRecord)
            && audit.Granted
            && audit.Actor?.DisplayName == "Dr. Rivera"
        );

        // 7-8. The patient revokes; the clinician's next load shows AccessEnded (Review Focus 3).
        await WithAsync(services =>
            services.GetRequiredService<GrantManager>().RevokeAsync(_sam, grant.Id)
        );
        HttpResponseMessage revoked = await browser.GetAsync(RecordPath());
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.StartsWith("/Clinician/AccessEnded", revoked.Headers.Location!.OriginalString);
        string ended = WebUtility.HtmlDecode(
            await browser.GetStringAsync(revoked.Headers.Location!.OriginalString)
        );
        Assert.Contains(Copy.AccessEnded, ended);

        // 9. The denied attempt is in Sam's activity too.
        Assert.Contains(await ActivityAsync(), audit =>
            audit.Action == nameof(AccessAction.ReadFullRecord)
            && !audit.Granted
            && audit.Reason == DenyReason.GrantRevoked
            && audit.Actor?.DisplayName == "Dr. Rivera"
        );
    }

    [Fact]
    public async Task Grant_expiring_while_the_record_is_open_shows_AccessEnded()
    {
        HttpClient browser = ClinicianBrowser();
        ShareOffer offer = await CreateOfferAsync();
        await RedeemAsync(browser, offer.Code);
        Grant grant = await ClinicianGrantAsync();
        await WithAsync(services =>
            services.GetRequiredService<GrantManager>().ApproveOrDenyAsync(_sam, grant.Id, true)
        );
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(RecordPath())).StatusCode);

        _app.Pki.Clock.Advance(TimeSpan.FromMinutes(6));
        HttpResponseMessage expired = await browser.GetAsync(RecordPath());

        Assert.Equal(HttpStatusCode.Redirect, expired.StatusCode);
        Assert.StartsWith("/Clinician/AccessEnded", expired.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Shift_ended_shows_AccessEnded_with_a_sign_in_link()
    {
        HttpClient browser = ClinicianBrowser();
        ShareOffer offer = await CreateOfferAsync();
        await RedeemAsync(browser, offer.Code);
        Grant grant = await ClinicianGrantAsync();
        await WithAsync(services =>
            services.GetRequiredService<GrantManager>().ApproveOrDenyAsync(_sam, grant.Id, true)
        );

        _app.Pki.Clock.Advance(TimeSpan.FromHours(10) + TimeSpan.FromMinutes(1));
        HttpResponseMessage shiftEnded = await browser.GetAsync(RecordPath());
        string ended = await browser.GetStringAsync(shiftEnded.Headers.Location!.OriginalString);

        Assert.StartsWith("/Clinician/AccessEnded", shiftEnded.Headers.Location!.OriginalString);
        Assert.Contains("Sign in again", ended);
    }

    private string RecordStatusPath()
    {
        return $"/Clinician/Record?handler=Status&patientId={_sam}";
    }

    private async Task<Grant> OpenRecordAsync(HttpClient browser)
    {
        ShareOffer offer = await CreateOfferAsync();
        await RedeemAsync(browser, offer.Code);
        Grant grant = await ClinicianGrantAsync();
        await WithAsync(services =>
            services.GetRequiredService<GrantManager>().ApproveOrDenyAsync(_sam, grant.Id, true)
        );
        string recordPage = await browser.GetStringAsync(RecordPath());
        Assert.Contains("every 2s", recordPage);
        return grant;
    }

    [Fact]
    public async Task Open_record_polls_without_auditing_and_locks_on_revoke()
    {
        HttpClient browser = ClinicianBrowser();
        Grant grant = await OpenRecordAsync(browser);
        int auditCount = (await ActivityAsync()).Count;

        HttpResponseMessage open = await browser.GetAsync(RecordStatusPath());
        await browser.GetAsync(RecordStatusPath());

        Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
        Assert.Equal(auditCount, (await ActivityAsync()).Count);

        await WithAsync(services =>
            services.GetRequiredService<GrantManager>().RevokeAsync(_sam, grant.Id)
        );
        HttpResponseMessage revoked = await browser.GetAsync(RecordStatusPath());

        Assert.Equal("/Clinician/AccessEnded", revoked.Headers.GetValues("HX-Redirect").Single());
        Assert.Equal(auditCount, (await ActivityAsync()).Count);
    }

    [Fact]
    public async Task Open_record_locks_on_expiry_and_on_shift_end()
    {
        HttpClient browser = ClinicianBrowser();
        await OpenRecordAsync(browser);

        _app.Pki.Clock.Advance(TimeSpan.FromMinutes(6));
        HttpResponseMessage expired = await browser.GetAsync(RecordStatusPath());
        _app.Pki.Clock.Advance(TimeSpan.FromHours(10));
        HttpResponseMessage shiftEnded = await browser.GetAsync(RecordStatusPath());

        Assert.Equal("/Clinician/AccessEnded", expired.Headers.GetValues("HX-Redirect").Single());
        Assert.Equal(
            "/Clinician/AccessEnded?reason=ShiftEnded",
            shiftEnded.Headers.GetValues("HX-Redirect").Single()
        );
    }

    [Fact]
    public async Task Same_code_entered_twice_shows_ShareCodeUsed()
    {
        ShareOffer offer = await CreateOfferAsync();
        await RedeemAsync(ClinicianBrowser(), offer.Code);

        HttpResponseMessage second = await RedeemAsync(ClinicianBrowser(), offer.Code);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains(
            Copy.ShareCodeUsed,
            WebUtility.HtmlDecode(await second.Content.ReadAsStringAsync())
        );
        await ClinicianGrantAsync();
    }

    [Fact]
    public async Task Clinician_pages_need_the_clinician_cookie()
    {
        HttpClient browser = _app.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );

        HttpResponseMessage record = await browser.GetAsync(RecordPath());

        Assert.Equal(HttpStatusCode.Redirect, record.StatusCode);
        Assert.StartsWith("/Clinician/Index", record.Headers.Location!.PathAndQuery);
    }
}
