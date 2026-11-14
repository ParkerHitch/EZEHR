using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Tests.Data;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Access;

public class AccessTests : IAsyncLifetime
{
    // 3:00 PM in Indianapolis.
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    private static readonly Party Rivera = new(
        PartyKind.Clinician,
        "urn:entra:tid:rivera",
        "Dr. Rivera",
        "Riverside Primary Care"
    );

    private static readonly Party Other = new(
        PartyKind.Clinician,
        "urn:entra:tid:other",
        "Dr. Other",
        "Riverside Primary Care"
    );

    private static readonly Party KidneyCheck = new(
        PartyKind.App,
        "urn:app:kidney-check",
        "Kidney Check",
        "Kidney Check"
    );

    private readonly FakeTimeProvider _clock = new(Start);
    private Guid _sam;
    private TestDatabase _database = null!;
    private TestServices _services = null!;

    private AuditManager Audit
    {
        get { return _services.Audit; }
    }

    private GrantManager Grants
    {
        get { return _services.Grants; }
    }

    private AccessManager Gate
    {
        get { return _services.Access; }
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

    private VerifiedCaller Clinician(Party party)
    {
        return new VerifiedCaller(party, _clock.GetUtcNow());
    }

    private Task<AccessDecision> ReadFullRecordAsync(Caller caller)
    {
        return Gate.AuthorizeAsync(_sam, AccessAction.ReadFullRecord, caller, null);
    }

    private async Task<Grant> ActiveGrantAsync(ShareDuration duration = ShareDuration.OneHour)
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, duration);
        GrantResult pending = await Grants.RedeemShareCodeAsync(offer.Code, Rivera);
        GrantResult active = await Grants.ApproveOrDenyAsync(
            _sam,
            pending.Grant!.Id,
            approve: true
        );
        return active.Grant!;
    }

    [Fact]
    public async Task No_grant_denied_and_logged_once()
    {
        AccessDecision decision = await ReadFullRecordAsync(Clinician(Rivera));
        Assert.Equal(new AccessDecision(false, null, DenyReason.NoGrant), decision);
        AuditEvent logged = Assert.Single(await Audit.ListForPatientAsync(_sam));
        Assert.False(logged.Granted);
        Assert.Equal(DenyReason.NoGrant, logged.Reason);
        Assert.Equal("ReadFullRecord", logged.Action);
        Assert.Equal(Rivera, logged.Actor);
    }

    [Fact]
    public async Task Unknown_patient_denied_and_not_logged()
    {
        Guid unknown = Guid.NewGuid();
        AccessDecision decision = await Gate.AuthorizeAsync(
            unknown,
            AccessAction.ReadFullRecord,
            new UnverifiedCaller(DenyReason.NoCertificate),
            "treatment"
        );
        Assert.False(decision.Allowed);
        Assert.Empty(await Audit.ListForPatientAsync(unknown));
        Assert.Empty(await Audit.ListForPatientAsync(_sam));
    }

    [Fact]
    public async Task Pending_denied()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        await Grants.RedeemShareCodeAsync(offer.Code, Rivera);
        Assert.Equal(
            DenyReason.GrantPending,
            (await ReadFullRecordAsync(Clinician(Rivera))).Reason
        );
    }

    [Fact]
    public async Task Active_allowed_and_logged()
    {
        Grant grant = await ActiveGrantAsync();
        int before = (await Audit.ListForPatientAsync(_sam)).Count;
        AccessDecision decision = await ReadFullRecordAsync(Clinician(Rivera));
        Assert.True(decision.Allowed);
        Assert.Equal(grant.Id, decision.Grant!.Id);
        IReadOnlyList<AuditEvent> events = await Audit.ListForPatientAsync(_sam);
        Assert.Equal(before + 1, events.Count);
        Assert.True(events[^1].Granted);
        Assert.Null(events[^1].Reason);
    }

    [Fact]
    public async Task Clinician_grant_is_read_only()
    {
        await ActiveGrantAsync();
        AccessDecision decision = await Gate.AuthorizeAsync(
            _sam,
            AccessAction.WriteSignedNote,
            Clinician(Rivera),
            null
        );
        Assert.Equal(DenyReason.ScopeDenied, decision.Reason);
    }

    [Fact]
    public async Task Grant_for_other_clinician_is_NoGrant()
    {
        await ActiveGrantAsync();
        Assert.Equal(DenyReason.NoGrant, (await ReadFullRecordAsync(Clinician(Other))).Reason);
        AccessDecision otherPatient = await Gate.AuthorizeAsync(
            Guid.NewGuid(),
            AccessAction.ReadFullRecord,
            Clinician(Rivera),
            null
        );
        Assert.Equal(DenyReason.NoGrant, otherPatient.Reason);
    }

    [Fact] // Review Focus 3
    public async Task Expired_marks_and_denies()
    {
        Grant grant = await ActiveGrantAsync(ShareDuration.FiveMinutes);
        Assert.Equal(Start.AddMinutes(5), grant.ExpiresAt);
        Assert.True((await ReadFullRecordAsync(Clinician(Rivera))).Allowed);
        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(
            DenyReason.GrantExpired,
            (await ReadFullRecordAsync(Clinician(Rivera))).Reason
        );
        Assert.Equal(GrantStatus.Expired, (await Grants.FindAsync(grant.Id))!.Status);
        Assert.Equal(
            DenyReason.GrantExpired,
            (await ReadFullRecordAsync(Clinician(Rivera))).Reason
        );
    }

    [Fact] // Review Focus 3
    public async Task Revoked_denies()
    {
        Grant grant = await ActiveGrantAsync();
        Assert.True((await ReadFullRecordAsync(Clinician(Rivera))).Allowed);
        Grant revoked = (await Grants.RevokeAsync(_sam, grant.Id)).Grant!;
        Assert.Equal(GrantStatus.Revoked, revoked.Status);
        Assert.Equal(Start, revoked.RevokedAt);
        Assert.Equal(
            DenyReason.GrantRevoked,
            (await ReadFullRecordAsync(Clinician(Rivera))).Reason
        );
    }

    [Fact]
    public async Task Denied_request_is_refused()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        GrantResult pending = await Grants.RedeemShareCodeAsync(offer.Code, Rivera);
        GrantResult denied = await Grants.ApproveOrDenyAsync(
            _sam,
            pending.Grant!.Id,
            approve: false
        );
        Assert.Equal(GrantStatus.Denied, denied.Grant!.Status);
        Assert.Equal(
            DenyReason.GrantRevoked,
            (await ReadFullRecordAsync(Clinician(Rivera))).Reason
        );
    }

    [Fact]
    public async Task New_share_after_expiry_is_allowed()
    {
        await ActiveGrantAsync(ShareDuration.FiveMinutes);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False((await ReadFullRecordAsync(Clinician(Rivera))).Allowed);
        await ActiveGrantAsync(ShareDuration.OneHour);
        Assert.True((await ReadFullRecordAsync(Clinician(Rivera))).Allowed);
    }

    [Fact] // Review Focus 2
    public async Task Redeem_twice_second_is_offer_used()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        Assert.Equal(8, offer.Code.Length);
        Assert.Equal(Start.AddMinutes(10), offer.OfferExpiresAt);
        // Typed as "abcd-efgh".
        GrantResult first = await Grants.RedeemShareCodeAsync(
            GrantManager.FormatCode(offer.Code).ToLowerInvariant(),
            Rivera
        );
        Assert.True(first.Succeeded);
        GrantResult second = await Grants.RedeemShareCodeAsync(offer.Code, Rivera);
        Assert.Equal(new GrantResult(false, null, GrantError.OfferUsed), second);
        IReadOnlyList<Grant> grants = await Grants.ListForPatientAsync(_sam);
        Assert.Equal(GrantStatus.Pending, Assert.Single(grants).Status);
    }

    [Fact] // Review Focus 2
    public async Task Concurrent_redeems_create_one_grant()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        GrantResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Grants.RedeemShareCodeAsync(offer.Code, Rivera))
        );
        Assert.Single(results, result => result.Succeeded);
        Assert.All(results.Where(result => !result.Succeeded), result =>
            Assert.Equal(GrantError.OfferUsed, result.Error)
        );
        Assert.Single(await Grants.ListForPatientAsync(_sam));
    }

    [Fact]
    public async Task Offer_expired_after_10_minutes()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Equal(
            GrantError.OfferExpired,
            (await Grants.RedeemShareCodeAsync(offer.Code, Rivera)).Error
        );
        Assert.Equal(
            GrantError.OfferUnknown,
            (await Grants.RedeemShareCodeAsync("ZZZZZZZZ", Rivera)).Error
        );
        Assert.Empty(await Grants.ListForPatientAsync(_sam));
    }

    [Fact]
    public async Task Redeem_logs_one_event_naming_the_clinician()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        await Grants.RedeemShareCodeAsync(offer.Code, Rivera);
        AuditEvent logged = Assert.Single(await Audit.ListForPatientAsync(_sam));
        Assert.Equal("RedeemShareCode", logged.Action);
        Assert.Equal(Rivera, logged.Actor);
        Assert.True(logged.Granted);
    }

    [Fact]
    public async Task Unverified_logged_with_claimed_purpose()
    {
        AccessDecision decision = await Gate.AuthorizeAsync(
            _sam,
            AccessAction.ReadFullRecord,
            new UnverifiedCaller(DenyReason.UntrustedCertificate),
            "treatment"
        );
        Assert.Equal(DenyReason.UntrustedCertificate, decision.Reason);
        AuditEvent logged = Assert.Single(await Audit.ListForPatientAsync(_sam));
        Assert.Null(logged.Actor);
        Assert.Equal("treatment", logged.ClaimedPurpose);
        Assert.False(logged.Granted);
    }

    [Fact]
    public async Task Long_claimed_purpose_is_cut_to_fit()
    {
        string purpose = new('x', 5000);
        await Gate.AuthorizeAsync(
            _sam,
            AccessAction.ReadFullRecord,
            new UnverifiedCaller(DenyReason.NoCertificate),
            purpose
        );
        AuditEvent logged = Assert.Single(await Audit.ListForPatientAsync(_sam));
        Assert.Equal(purpose[..AuditManager.MaxClaimedPurposeLength], logged.ClaimedPurpose);
    }

    [Fact]
    public async Task App_scope_cannot_read_record()
    {
        Grant grant = await Grants.CreateAppGrantAsync(_sam, KidneyCheck);
        Assert.Equal(
            (GrantScope.LabsAndNotes, GrantStatus.Active, (DateTimeOffset?)null),
            (grant.Scope, grant.Status, grant.ExpiresAt)
        );
        // Idempotent.
        Assert.Equal(grant.Id, (await Grants.CreateAppGrantAsync(_sam, KidneyCheck)).Id);
        VerifiedCaller app = new(KidneyCheck, null);
        Assert.Equal(DenyReason.ScopeDenied, (await ReadFullRecordAsync(app)).Reason);
        Assert.True(
            (await Gate.AuthorizeAsync(_sam, AccessAction.ReadEgfrLabs, app, null)).Allowed
        );
        Assert.True(
            (await Gate.AuthorizeAsync(_sam, AccessAction.WriteSignedNote, app, null)).Allowed
        );
        _clock.Advance(TimeSpan.FromDays(365));
        // Ongoing.
        Assert.True(
            (await Gate.AuthorizeAsync(_sam, AccessAction.ReadEgfrLabs, app, null)).Allowed
        );
        await Grants.RevokeAsync(_sam, grant.Id);
        AccessDecision revoked = await Gate.AuthorizeAsync(
            _sam,
            AccessAction.ReadEgfrLabs,
            app,
            null
        );
        Assert.Equal(DenyReason.GrantRevoked, revoked.Reason);
    }

    [Fact]
    public async Task Clinician_shift_over_10h_denied()
    {
        await ActiveGrantAsync(ShareDuration.EndOfDay);
        VerifiedCaller pastShift = new(Rivera, Start.AddHours(-10).AddSeconds(-1));
        Assert.Equal(DenyReason.ShiftEnded, (await ReadFullRecordAsync(pastShift)).Reason);
        VerifiedCaller inShift = new(Rivera, Start.AddHours(-9));
        Assert.True((await ReadFullRecordAsync(inShift)).Allowed);
        VerifiedCaller notSignedIn = new(Rivera, null);
        Assert.Equal(DenyReason.NotSignedIn, (await ReadFullRecordAsync(notSignedIn)).Reason);
    }

    [Fact]
    public async Task EndOfDay_is_local_midnight_minus_1s()
    {
        Grant grant = await ActiveGrantAsync(ShareDuration.EndOfDay);
        // 3:00 PM EST on Nov 13 → 11:59:59 PM EST = 04:59:59 UTC on Nov 14.
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 4, 59, 59, TimeSpan.Zero), grant.ExpiresAt);
        Assert.Null(grant.DurationMinutes);
    }

    [Fact]
    public async Task Durations_match_offer()
    {
        Assert.Equal(
            Start.AddHours(4),
            (await ActiveGrantAsync(ShareDuration.FourHours)).ExpiresAt
        );
        Assert.Equal(240, (await Grants.ListForPatientAsync(_sam)).Single().DurationMinutes);
    }

    [Fact]
    public async Task Decide_rejects_other_patients_and_non_pending()
    {
        ShareOffer offer = await Grants.CreateShareOfferAsync(_sam, ShareDuration.OneHour);
        Guid grantId = (await Grants.RedeemShareCodeAsync(offer.Code, Rivera)).Grant!.Id;
        Assert.Equal(
            GrantError.GrantUnknown,
            (await Grants.ApproveOrDenyAsync(Guid.NewGuid(), grantId, true)).Error
        );
        await Grants.ApproveOrDenyAsync(_sam, grantId, true);
        Assert.Equal(
            GrantError.GrantNotPending,
            (await Grants.ApproveOrDenyAsync(_sam, grantId, true)).Error
        );
        Assert.Equal(
            GrantError.GrantUnknown,
            (await Grants.RevokeAsync(Guid.NewGuid(), grantId)).Error
        );
    }
}
