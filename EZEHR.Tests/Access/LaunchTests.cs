using Dapper;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Access;

/// <summary>
/// The organization launch: a clinician can only ask for a record through a launch their own organization's system
/// made for them, and a share code only works from the chart of the patient it belongs to.
/// </summary>
public class LaunchTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    private static readonly Guid RiversideTenant = Guid.Parse(
        "8f6c1c3e-2d4b-4b7a-9a51-1f0e2c7d9b10"
    );

    private static readonly Guid RiveraObjectId = Guid.Parse(
        "c2a7e5d1-9b3f-4e8a-a6c4-3d2b1e0f9a87"
    );

    private static readonly Guid OkaforObjectId = Guid.Parse(
        "0d9f4b72-6a1e-4c35-8b27-5e4f3a2d1c90"
    );

    private static readonly Party Rivera = Clinician(RiveraObjectId, "Dr. Rivera");

    private static readonly Party Okafor = Clinician(OkaforObjectId, "Dr. Okafor");

    private static readonly VerifiedCaller Riverside = new(
        OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("riverside"))!,
        null
    );

    private const string SamsChart = "RPC-1107";

    private const string Visit = "ENC-2026-11-13-07";

    /// <summary>
    /// SHA-256 of the twelve-value JSON array for Dr. Rivera reading the all-zero patient at Start, worked out by
    /// hand outside this code base: what the chain stored for such an event before visits were recorded.
    /// </summary>
    private const string PreVisitFingerprint =
        "33e4973a7d2fe25b036caa00227448285423e13a50ff261567f14d7d710d33d1";

    private readonly FakeTimeProvider _clock = new(Start);
    private Guid _sam;
    private TestDatabase _database = null!;
    private TestServices _services = null!;

    private LaunchManager Launches
    {
        get { return _services.Launches; }
    }

    private GrantManager Grants
    {
        get { return _services.Grants; }
    }

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
        _services = new TestServices(_database.Db, _clock);
        _sam = await _services.Records.CreatePatientAsync("Sam Taylor");
        await _database.SetEntraTenantAsync(Riverside.Party.SubjectUri, RiversideTenant);
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id)
            VALUES (@patientId, @providerUri, @providerPatientId)
            """,
            new
            {
                patientId = _sam,
                providerUri = Riverside.Party.SubjectUri,
                providerPatientId = SamsChart,
            }
        );
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private static Party Clinician(Guid objectId, string name)
    {
        return new Party(
            PartyKind.Clinician,
            OrgDirectory.ClinicianUri(RiversideTenant.ToString(), objectId.ToString()),
            name,
            "Riverside Primary Care",
            "urn:provider:riverside"
        );
    }

    /// <summary>Riverside's system makes a launch for Dr. Rivera; returns its code.</summary>
    private async Task<string> CreateAsync(string providerPatientId = SamsChart)
    {
        CreateClinicianLaunchResult created = await Launches.CreateClinicianLaunchAsync(
            Riverside,
            RiveraObjectId,
            providerPatientId,
            Visit
        );
        Assert.True(created.Allowed && created.Error is null);
        return created.LaunchCode!;
    }

    /// <summary>Made by Riverside and opened by Dr. Rivera.</summary>
    private async Task<Guid> OpenAsync(string providerPatientId = SamsChart)
    {
        OpenClinicianLaunchResult opened = await Launches.OpenClinicianLaunchAsync(
            await CreateAsync(providerPatientId),
            Rivera
        );
        return opened.Launch!.Id;
    }

    [Fact]
    public async Task Only_a_provider_with_clinicians_can_make_a_launch()
    {
        VerifiedCaller lakeside = new(
            OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("lakeside"))!,
            null
        );
        VerifiedCaller kidneyCheck = new(
            OrgDirectory.Demo.FindByUri(OrgDirectory.KidneyCheckUri)!,
            null
        );

        CreateClinicianLaunchResult made = await Launches.CreateClinicianLaunchAsync(
            Riverside,
            RiveraObjectId,
            SamsChart,
            Visit
        );
        CreateClinicianLaunchResult noTenant = await Launches.CreateClinicianLaunchAsync(
            lakeside,
            RiveraObjectId,
            "LUC-20417",
            Visit
        );
        CreateClinicianLaunchResult app = await Launches.CreateClinicianLaunchAsync(
            kidneyCheck,
            RiveraObjectId,
            SamsChart,
            Visit
        );
        CreateClinicianLaunchResult nobody = await Launches.CreateClinicianLaunchAsync(
            new UnverifiedCaller(DenyReason.NoCertificate),
            RiveraObjectId,
            SamsChart,
            Visit
        );

        Assert.True(made.Allowed);
        Assert.Null(made.Error);
        Assert.EndsWith($"/Clinician/Launch?launch={made.LaunchCode}", made.LaunchUrl);
        Assert.Equal(LaunchError.ProviderHasNoClinicians, noTenant.Error);
        Assert.Equal(DenyReason.WrongPartyKind, app.Reason);
        Assert.Equal(DenyReason.NoCertificate, nobody.Reason);
    }

    [Fact]
    public async Task A_launch_opens_only_for_the_clinician_it_was_made_for()
    {
        string code = await CreateAsync();

        OpenClinicianLaunchResult other = await Launches.OpenClinicianLaunchAsync(code, Okafor);
        OpenClinicianLaunchResult unknown = await Launches.OpenClinicianLaunchAsync(
            "ZZZZ-ZZZZ",
            Rivera
        );
        OpenClinicianLaunchResult opened = await Launches.OpenClinicianLaunchAsync(
            GrantManager.FormatCode(code).ToLowerInvariant(),
            Rivera
        );
        OpenClinicianLaunchResult again = await Launches.OpenClinicianLaunchAsync(code, Rivera);

        Assert.Equal(LaunchError.LaunchForAnotherClinician, other.Error);
        Assert.Equal(LaunchError.LaunchUnknown, unknown.Error);
        Assert.True(opened.Succeeded);
        Assert.Equal(
            new ClinicianLaunch(
                opened.Launch!.Id,
                "urn:provider:riverside",
                Rivera.SubjectUri,
                SamsChart,
                Visit,
                Start + LaunchManager.LaunchLifetime
            ),
            opened.Launch
        );
        // A reload gives the same launch, not an error.
        Assert.Equal(opened.Launch, again.Launch);
        Assert.Null(await Launches.FindOpenLaunchAsync(opened.Launch.Id, Okafor));
    }

    [Fact]
    public async Task A_launch_must_be_opened_within_minutes_and_runs_out_after_an_hour()
    {
        string stale = await CreateAsync();
        _clock.Advance(LaunchManager.LaunchOpenWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(
            LaunchError.LaunchExpired,
            (await Launches.OpenClinicianLaunchAsync(stale, Rivera)).Error
        );

        string code = await CreateAsync();
        Guid launchId = (await Launches.OpenClinicianLaunchAsync(code, Rivera)).Launch!.Id;
        _clock.Advance(LaunchManager.LaunchLifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(await Launches.FindOpenLaunchAsync(launchId, Rivera));
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(await Launches.FindOpenLaunchAsync(launchId, Rivera));
        Assert.Equal(
            LaunchError.LaunchExpired,
            (await Launches.OpenClinicianLaunchAsync(code, Rivera)).Error
        );
    }

    [Fact]
    public async Task Without_an_open_launch_of_their_own_a_clinician_cannot_use_a_share_code()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        Guid riverasLaunch = await OpenAsync();
        string unopened = await CreateAsync();

        GrantResult noLaunch = await Grants.RedeemShareCodeAsync(
            offer.Code,
            Rivera,
            Guid.NewGuid()
        );
        GrantResult someoneElses = await Grants.RedeemShareCodeAsync(
            offer.Code,
            Okafor,
            riverasLaunch
        );
        _clock.Advance(LaunchManager.LaunchLifetime);
        GrantResult ranOut = await Grants.RedeemShareCodeAsync(offer.Code, Rivera, riverasLaunch);

        Assert.Equal(GrantError.LaunchNotOpen, noLaunch.Error);
        Assert.Equal(GrantError.LaunchNotOpen, someoneElses.Error);
        Assert.Equal(GrantError.LaunchNotOpen, ranOut.Error);
        Assert.NotEmpty(unopened);
        Assert.Empty(await Grants.ListForPatientAsync(_sam));
        Assert.Empty(await _services.Audit.ListForPatientAsync(_sam));
    }

    [Fact]
    public async Task A_share_code_used_from_another_patients_chart_is_refused_logged_and_left_unused()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        Guid wrongChart = await OpenAsync("RPC-9999");

        GrantResult refused = await Grants.RedeemShareCodeAsync(offer.Code, Rivera, wrongChart);

        Assert.Equal(GrantError.OfferNotForLaunchedPatient, refused.Error);
        Assert.Empty(await Grants.ListForPatientAsync(_sam));
        AuditEvent blocked = Assert.Single(await _services.Audit.ListForPatientAsync(_sam));
        Assert.False(blocked.Granted);
        Assert.Equal(DenyReason.LaunchPatientMismatch, blocked.Reason);
        Assert.Equal(Visit, blocked.EncounterId);
        Assert.Equal(
            "Blocked: Dr. Rivera, Riverside Primary Care, asked from another patient's chart",
            Copy.ActivityLine(blocked)
        );

        // The code wasn't used up: from Sam's own chart it still works.
        GrantResult redeemed = await Grants.RedeemShareCodeAsync(
            offer.Code,
            Rivera,
            await OpenAsync()
        );
        Assert.True(redeemed.Succeeded);
    }

    [Fact]
    public async Task The_visit_follows_the_grant_into_every_audit_line_and_the_organizations_audit()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);

        GrantResult pending = await Grants.RedeemShareCodeAsync(
            offer.Code,
            Rivera,
            await OpenAsync()
        );
        await Grants.ApproveOrDenyAsync(_sam, pending.Grant!.Id, true);
        AccessDecision read = await _services.Access.AuthorizeAsync(
            _sam,
            AccessAction.ReadFullRecord,
            new VerifiedCaller(Rivera, Start),
            null
        );

        Assert.True(read.Allowed);
        Assert.Equal(Visit, pending.Grant.EncounterId);
        Assert.Equal(Visit, (await Grants.FindAsync(pending.Grant.Id))!.EncounterId);
        IReadOnlyList<AuditEvent> log = await _services.Audit.ListForPatientAsync(_sam);
        Assert.Equal(["RedeemShareCode", "ReadFullRecord"], log.Select(logged => logged.Action));
        Assert.All(log, logged => Assert.Equal(Visit, logged.EncounterId));
        Assert.True(AuditChain.Verify(log).Intact);
        ReadOrganizationAuditResult audit =
            await _services.OrganizationAudit.ReadOrganizationAuditAsync(
                Riverside,
                new OrganizationAuditFilter(null, null, null, null, null, 0, 100)
            );
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, logged => Assert.Equal(Visit, logged.EncounterId));
        Assert.All(audit.Events, logged => Assert.Equal(SamsChart, logged.ProviderPatientId));
    }

    [Fact]
    public async Task Emergency_access_needs_a_launch_too_but_not_the_patients_own_chart()
    {
        string emergencyCode = await Grants.TurnOnEmergencyAccessAsync(_sam);

        GrantResult noLaunch = await Grants.OpenEmergencyAccessAsync(
            emergencyCode,
            "Unresponsive",
            Rivera,
            Guid.NewGuid()
        );
        GrantResult opened = await Grants.OpenEmergencyAccessAsync(
            emergencyCode,
            "Unresponsive",
            Rivera,
            await OpenAsync("RPC-ER-0042")
        );

        Assert.Equal(GrantError.LaunchNotOpen, noLaunch.Error);
        Assert.True(opened.Succeeded);
        Assert.Equal(Visit, opened.Grant!.EncounterId);
        AuditEvent logged = Assert.Single(await _services.Audit.ListForPatientAsync(_sam));
        Assert.Equal(GrantManager.OpenEmergencyAccessAction, logged.Action);
        Assert.Equal(Visit, logged.EncounterId);
    }

    [Fact]
    public void An_event_without_a_visit_keeps_the_fingerprint_it_had_before_visits_were_recorded()
    {
        string withoutVisit = AuditChain.ComputeHash(
            null,
            Guid.Empty,
            Start,
            Rivera,
            "ReadFullRecord",
            null,
            true,
            null
        );
        string withVisit = AuditChain.ComputeHash(
            null,
            Guid.Empty,
            Start,
            Rivera,
            "ReadFullRecord",
            null,
            true,
            null,
            Visit
        );

        // This event's fingerprint as the chain computed it before visits existed: twelve values, no thirteenth.
        Assert.Equal(PreVisitFingerprint, withoutVisit);
        Assert.NotEqual(withoutVisit, withVisit);
    }
}
