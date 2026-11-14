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
        _clinicianApp = TestClinicianAuthHandler.Install(_app);
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
            Party provider = OrgDirectory.FindBySubjectUri(OrgDirectory.ProviderUri(slug))!;
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

        Party kidneyCheck = OrgDirectory.FindBySubjectUri("urn:app:kidney-check")!;
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

    /// <summary>Fills in and submits the Redeem form, antiforgery token included.</summary>
    private static async Task<HttpResponseMessage> RedeemAsync(HttpClient browser, string code)
    {
        string form = await browser.GetStringAsync("/Clinician/Redeem");
        string token = Regex
            .Match(form, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")
            .Groups[1]
            .Value;
        return await browser.PostAsync(
            "/Clinician/Redeem",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["Code"] = code,
                    ["__RequestVerificationToken"] = token,
                }
            )
        );
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
