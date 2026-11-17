using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Tests.Data;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Access;

/// <summary>Break-glass: the patient's emergency code, a stated reason, one hour, and a log line for both sides.</summary>
public class EmergencyAccessTests : IAsyncLifetime
{
    // 3:00 PM in Indianapolis.
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    private const string Reason = "Unresponsive after a car crash";

    private static readonly Party Rivera = new(
        PartyKind.Clinician,
        "urn:entra:tid:rivera",
        "Dr. Rivera",
        "Riverside Primary Care",
        "urn:provider:riverside"
    );

    private static readonly VerifiedCaller RiveraOnShift = new(Rivera, Start);

    private readonly FakeTimeProvider _clock = new(Start);
    private Guid _sam;
    private TestDatabase _database = null!;
    private TestServices _services = null!;

    private GrantManager Grants
    {
        get { return _services.Grants; }
    }

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
        _services = new TestServices(_database.Db, _clock);
        _sam = await _services.Records.CreatePatientAsync("Sam Taylor");
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    /// <summary>The clinician asks from a launch their organization made, from a chart EZEHR can't match to anyone.</summary>
    private async Task<GrantResult> OpenEmergencyAsync(
        string emergencyCode,
        string? reason,
        Party clinician
    )
    {
        Guid launchId = await TestLaunches.OpenAsync(_database.Db, _clock, clinician, null);
        return await Grants.OpenEmergencyAccessAsync(emergencyCode, reason, clinician, launchId);
    }

    private async Task<AccessDecision> ReadAsync()
    {
        return await _services.Access.AuthorizeAsync(
            _sam,
            AccessAction.ReadFullRecord,
            RiveraOnShift,
            null
        );
    }

    [Fact]
    public async Task Emergency_access_is_off_until_the_patient_turns_it_on()
    {
        AccessOverview overview = await Grants.ListAccessAsync(_sam);
        GrantResult blank = await OpenEmergencyAsync("", Reason, Rivera);
        GrantResult guessed = await OpenEmergencyAsync("ABCD-EFGH", Reason, Rivera);

        Assert.Null(overview.EmergencyCode);
        Assert.Equal(GrantError.EmergencyCodeUnknown, blank.Error);
        Assert.Equal(GrantError.EmergencyCodeUnknown, guessed.Error);
        Assert.Empty(await Grants.ListForPatientAsync(_sam));
        Assert.Empty(await _services.Audit.ListForPatientAsync(_sam));
    }

    [Fact]
    public async Task Code_and_reason_open_the_record_for_one_hour_without_approval()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);

        GrantResult opened = await OpenEmergencyAsync(
            GrantManager.FormatCode(code).ToLowerInvariant(),
            $"  {Reason}  ",
            Rivera
        );

        Assert.True(opened.Succeeded);
        Grant grant = opened.Grant!;
        Assert.Equal(GrantStatus.Active, grant.Status);
        Assert.Equal(GrantScope.Full, grant.Scope);
        Assert.Equal(Reason, grant.EmergencyReason);
        Assert.Null(grant.ApprovedAt);
        Assert.Equal(Start.AddHours(1), grant.ExpiresAt);
        Assert.True((await ReadAsync()).Allowed);

        IReadOnlyList<AuditEvent> log = await _services.Audit.ListForPatientAsync(_sam);
        Assert.Equal(2, log.Count);
        Assert.Equal(GrantManager.OpenEmergencyAccessAction, log[0].Action);
        Assert.Equal(Reason, log[0].ClaimedPurpose);
        Assert.Equal(Rivera, log[0].Actor);
        Assert.Equal(nameof(AccessAction.ReadFullRecord), log[1].Action);
        Assert.Equal(GrantManager.EmergencyPurpose, log[1].ClaimedPurpose);
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);

        GrantResult missing = await OpenEmergencyAsync(code, null, Rivera);
        GrantResult blank = await OpenEmergencyAsync(code, "   ", Rivera);

        Assert.Equal(GrantError.EmergencyReasonMissing, missing.Error);
        Assert.Equal(GrantError.EmergencyReasonMissing, blank.Error);
        Assert.Empty(await Grants.ListForPatientAsync(_sam));
        Assert.False((await ReadAsync()).Allowed);
    }

    [Fact]
    public async Task A_long_reason_is_cut_to_fit()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);

        GrantResult opened = await OpenEmergencyAsync(
            code,
            new string('x', GrantManager.MaxEmergencyReasonLength + 50),
            Rivera
        );

        Assert.Equal(GrantManager.MaxEmergencyReasonLength, opened.Grant!.EmergencyReason!.Length);
    }

    [Fact]
    public async Task A_new_code_replaces_the_old_one_and_turning_off_removes_it()
    {
        string first = await Grants.TurnOnEmergencyAccessAsync(_sam);
        string second = await Grants.TurnOnEmergencyAccessAsync(_sam);
        Assert.NotEqual(first, second);
        Assert.Equal(
            GrantManager.FormatCode(second),
            (await Grants.ListAccessAsync(_sam)).EmergencyCode
        );
        Assert.Equal(
            GrantError.EmergencyCodeUnknown,
            (await OpenEmergencyAsync(first, Reason, Rivera)).Error
        );

        await Grants.TurnOffEmergencyAccessAsync(_sam);

        Assert.Null((await Grants.ListAccessAsync(_sam)).EmergencyCode);
        Assert.Equal(
            GrantError.EmergencyCodeUnknown,
            (await OpenEmergencyAsync(second, Reason, Rivera)).Error
        );
    }

    [Fact]
    public async Task Emergency_access_ends_after_an_hour()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);
        await OpenEmergencyAsync(code, Reason, Rivera);

        _clock.Advance(TimeSpan.FromMinutes(59));
        Assert.True((await ReadAsync()).Allowed);
        _clock.Advance(TimeSpan.FromMinutes(1));
        AccessDecision late = await ReadAsync();

        Assert.False(late.Allowed);
        Assert.Equal(DenyReason.GrantExpired, late.Reason);
    }

    [Fact]
    public async Task The_patient_can_revoke_emergency_access()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);
        GrantResult opened = await OpenEmergencyAsync(code, Reason, Rivera);

        await Grants.RevokeAsync(_sam, opened.Grant!.Id);
        AccessDecision after = await ReadAsync();

        Assert.False(after.Allowed);
        Assert.Equal(DenyReason.GrantRevoked, after.Reason);
    }

    [Fact]
    public async Task Turning_emergency_access_off_leaves_open_access_to_run_out()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);
        await OpenEmergencyAsync(code, Reason, Rivera);

        await Grants.TurnOffEmergencyAccessAsync(_sam);

        Assert.True((await ReadAsync()).Allowed);
    }

    [Fact]
    public async Task The_patient_sees_who_opened_it_and_why()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);
        GrantResult opened = await OpenEmergencyAsync(code, Reason, Rivera);
        await ReadAsync();

        AccessOverview overview = await Grants.ListAccessAsync(_sam);
        IReadOnlyList<AuditEvent> log = await _services.Audit.ListForPatientAsync(_sam);

        AccessLine line = Assert.Single(overview.Lines);
        Assert.True(line.Emergency);
        Assert.True(line.CanRevoke);
        Assert.Equal(
            $"Emergency access: Dr. Rivera, Riverside Primary Care · until 4:00 PM · reason given: {Reason}",
            line.Text
        );
        Assert.Equal(
            $"Emergency access: Dr. Rivera, Riverside Primary Care, opened your record without your approval. Reason given: {Reason}",
            Copy.ActivityLine(log[0])
        );
        Assert.Equal(
            "Dr. Rivera, Riverside Primary Care, viewed your record (emergency access)",
            Copy.ActivityLine(log[1])
        );

        await Grants.RevokeAsync(_sam, opened.Grant!.Id);
        AccessLine ended = Assert.Single((await Grants.ListAccessAsync(_sam)).Lines);
        Assert.False(ended.CanRevoke);
        Assert.Equal(
            "Dr. Rivera, Riverside Primary Care (emergency access) · you revoked access",
            ended.Text
        );
    }

    [Fact]
    public async Task The_clinicians_organization_sees_the_emergency_access_and_its_reason()
    {
        string code = await Grants.TurnOnEmergencyAccessAsync(_sam);
        await OpenEmergencyAsync(code, Reason, Rivera);
        Party riverside = new(
            PartyKind.Provider,
            "urn:provider:riverside",
            "Riverside Primary Care",
            "Riverside Primary Care",
            "urn:provider:riverside"
        );

        ReadOrganizationAuditResult audit =
            await _services.OrganizationAudit.ReadOrganizationAuditAsync(
                new VerifiedCaller(riverside, null),
                new OrganizationAuditFilter(null, null, null, null, null, 0, 100)
            );

        OrganizationAuditEvent opened = Assert.Single(audit.Events);
        Assert.Equal(GrantManager.OpenEmergencyAccessAction, opened.Action);
        Assert.Equal(Reason, opened.ClaimedPurpose);
        Assert.Equal(Rivera.SubjectUri, opened.ActorUri);
    }
}
