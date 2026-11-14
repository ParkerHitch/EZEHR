using Dapper;
using EZEHR.Access;
using EZEHR.Data;
using EZEHR.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Access;

public class AccessTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero); // 3:00 PM in Indianapolis

    private readonly FakeTimeProvider clock = new(Start);
    private readonly SqliteConnection db = Db.Open(":memory:");
    private readonly AuditLog audit;
    private readonly GrantService grants;
    private readonly AccessGate gate;
    private readonly Guid sam = Guid.NewGuid();

    private static readonly Party Rivera = new(PartyKind.Clinician, "urn:entra:tid:rivera", "Dr. Rivera", "Riverside Primary Care");
    private static readonly Party Other = new(PartyKind.Clinician, "urn:entra:tid:other", "Dr. Other", "Riverside Primary Care");
    private static readonly Party KidneyCheck = new(PartyKind.App, "urn:app:kidney-check", "Kidney Check", "Kidney Check");

    public AccessTests()
    {
        audit = new AuditLog(db, clock);
        grants = new GrantService(db, audit, clock, Options.Create(new AppOptions()));
        gate = new AccessGate(grants, audit, clock);
    }

    private VerifiedCaller Clinician(Party p) => new(p, clock.GetUtcNow());

    private Grant ActiveGrant(ShareDuration d = ShareDuration.OneHour)
    {
        var offer = grants.CreateOffer(sam, d);
        var pending = grants.Redeem(offer.Code, Rivera);
        return grants.Decide(sam, pending.Id, approve: true);
    }

    [Fact]
    public void No_grant_denied_and_logged_once()
    {
        var d = gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null);
        Assert.Equal(new AccessDecision(false, null, DenyReason.NoGrant), d);
        var e = Assert.Single(audit.List(sam));
        Assert.False(e.Granted);
        Assert.Equal(DenyReason.NoGrant, e.Reason);
        Assert.Equal("ReadRecord", e.Action);
        Assert.Equal(Rivera, e.Actor);
    }

    [Fact]
    public void Pending_denied()
    {
        var offer = grants.CreateOffer(sam, ShareDuration.OneHour);
        grants.Redeem(offer.Code, Rivera);
        Assert.Equal(DenyReason.GrantPending, gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Reason);
    }

    [Fact]
    public void Active_allowed_and_logged()
    {
        var grant = ActiveGrant();
        var before = audit.List(sam).Count;
        var d = gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null);
        Assert.True(d.Allowed);
        Assert.Equal(grant.Id, d.Grant!.Id);
        var events = audit.List(sam);
        Assert.Equal(before + 1, events.Count);
        Assert.True(events[^1].Granted);
        Assert.Null(events[^1].Reason);
    }

    [Fact]
    public void Grant_for_other_clinician_is_NoGrant()
    {
        ActiveGrant();
        Assert.Equal(DenyReason.NoGrant, gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Other), null).Reason);
        Assert.Equal(DenyReason.NoGrant, gate.Authorize(Guid.NewGuid(), AccessAction.ReadRecord, Clinician(Rivera), null).Reason);
    }

    [Fact] // Review Focus 3
    public void Expired_marks_and_denies()
    {
        var grant = ActiveGrant(ShareDuration.FiveMinutes);
        Assert.Equal(Start.AddMinutes(5), grant.ExpiresAt);
        Assert.True(gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Allowed);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(DenyReason.GrantExpired, gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Reason);
        Assert.Equal(GrantStatus.Expired, grants.Find(grant.Id)!.Status);
        Assert.Equal(DenyReason.GrantExpired, gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Reason);
    }

    [Fact] // Review Focus 3
    public void Revoked_denies()
    {
        var grant = ActiveGrant();
        Assert.True(gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Allowed);
        var revoked = grants.Revoke(sam, grant.Id);
        Assert.Equal(GrantStatus.Revoked, revoked.Status);
        Assert.Equal(Start, revoked.RevokedAt);
        Assert.Equal(DenyReason.GrantRevoked, gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Reason);
    }

    [Fact]
    public void Denied_request_is_refused()
    {
        var offer = grants.CreateOffer(sam, ShareDuration.OneHour);
        var pending = grants.Redeem(offer.Code, Rivera);
        Assert.Equal(GrantStatus.Denied, grants.Decide(sam, pending.Id, approve: false).Status);
        Assert.Equal(DenyReason.GrantRevoked, gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Reason);
    }

    [Fact]
    public void New_share_after_expiry_is_allowed()
    {
        ActiveGrant(ShareDuration.FiveMinutes);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Allowed);
        ActiveGrant(ShareDuration.OneHour);
        Assert.True(gate.Authorize(sam, AccessAction.ReadRecord, Clinician(Rivera), null).Allowed);
    }

    [Fact] // Review Focus 2
    public void Redeem_twice_second_throws_offer_used()
    {
        var offer = grants.CreateOffer(sam, ShareDuration.OneHour);
        Assert.Equal(8, offer.Code.Length);
        Assert.Equal(Start.AddMinutes(10), offer.OfferExpiresAt);
        grants.Redeem(offer.DisplayCode.ToLowerInvariant(), Rivera); // typed as "abcd-efgh"
        var e = Assert.Throws<GrantException>(() => grants.Redeem(offer.Code, Rivera));
        Assert.Equal("offer_used", e.Code);
        Assert.Single(grants.ForPatient(sam), g => g.Status == GrantStatus.Pending);
        Assert.Single(grants.ForPatient(sam));
    }

    [Fact]
    public void Offer_expired_after_10_minutes()
    {
        var offer = grants.CreateOffer(sam, ShareDuration.OneHour);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Equal("offer_expired", Assert.Throws<GrantException>(() => grants.Redeem(offer.Code, Rivera)).Code);
        Assert.Equal("offer_unknown", Assert.Throws<GrantException>(() => grants.Redeem("ZZZZZZZZ", Rivera)).Code);
        Assert.Empty(grants.ForPatient(sam));
    }

    [Fact]
    public void Redeem_logs_one_event_naming_the_clinician()
    {
        var offer = grants.CreateOffer(sam, ShareDuration.OneHour);
        grants.Redeem(offer.Code, Rivera);
        var e = Assert.Single(audit.List(sam));
        Assert.Equal("RedeemOffer", e.Action);
        Assert.Equal(Rivera, e.Actor);
        Assert.True(e.Granted);
    }

    [Fact]
    public void Unverified_logged_with_claimed_purpose()
    {
        var d = gate.Authorize(sam, AccessAction.ReadRecord, new UnverifiedCaller(DenyReason.UntrustedCertificate), "treatment");
        Assert.Equal(DenyReason.UntrustedCertificate, d.Reason);
        var e = Assert.Single(audit.List(sam));
        Assert.Null(e.Actor);
        Assert.Equal("treatment", e.ClaimedPurpose);
        Assert.False(e.Granted);
    }

    [Fact]
    public void App_scope_cannot_read_record()
    {
        var g = grants.CreateAppGrant(sam, KidneyCheck);
        Assert.Equal((GrantScope.LabsAndNotes, GrantStatus.Active, (DateTimeOffset?)null), (g.Scope, g.Status, g.ExpiresAt));
        Assert.Equal(g.Id, grants.CreateAppGrant(sam, KidneyCheck).Id); // idempotent
        var app = new VerifiedCaller(KidneyCheck, null);
        Assert.Equal(DenyReason.ScopeDenied, gate.Authorize(sam, AccessAction.ReadRecord, app, null).Reason);
        Assert.True(gate.Authorize(sam, AccessAction.ReadLabs, app, null).Allowed);
        Assert.True(gate.Authorize(sam, AccessAction.WriteNote, app, null).Allowed);
        clock.Advance(TimeSpan.FromDays(365));
        Assert.True(gate.Authorize(sam, AccessAction.ReadLabs, app, null).Allowed); // ongoing
        grants.Revoke(sam, g.Id);
        Assert.Equal(DenyReason.GrantRevoked, gate.Authorize(sam, AccessAction.ReadLabs, app, null).Reason);
    }

    [Fact]
    public void Clinician_shift_over_10h_denied()
    {
        ActiveGrant(ShareDuration.EndOfDay);
        var signedIn = new VerifiedCaller(Rivera, Start.AddHours(-10).AddSeconds(-1));
        Assert.Equal(DenyReason.ShiftEnded, gate.Authorize(sam, AccessAction.ReadRecord, signedIn, null).Reason);
        Assert.True(gate.Authorize(sam, AccessAction.ReadRecord, new VerifiedCaller(Rivera, Start.AddHours(-9)), null).Allowed);
        Assert.Equal(DenyReason.NotSignedIn, gate.Authorize(sam, AccessAction.ReadRecord, new VerifiedCaller(Rivera, null), null).Reason);
    }

    [Fact]
    public void EndOfDay_is_local_midnight_minus_1s()
    {
        var g = ActiveGrant(ShareDuration.EndOfDay);
        // 3:00 PM EST on Nov 13 → 11:59:59 PM EST = 04:59:59 UTC on Nov 14
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 4, 59, 59, TimeSpan.Zero), g.ExpiresAt);
        Assert.Null(g.DurationMinutes);
    }

    [Fact]
    public void Durations_match_offer()
    {
        Assert.Equal(Start.AddHours(4), ActiveGrant(ShareDuration.FourHours).ExpiresAt);
        Assert.Equal(240, grants.ForPatient(sam).Single().DurationMinutes);
    }

    [Fact]
    public void Decide_rejects_other_patients_and_non_pending()
    {
        var offer = grants.CreateOffer(sam, ShareDuration.OneHour);
        var pending = grants.Redeem(offer.Code, Rivera);
        Assert.Equal("grant_unknown", Assert.Throws<GrantException>(() => grants.Decide(Guid.NewGuid(), pending.Id, true)).Code);
        grants.Decide(sam, pending.Id, true);
        Assert.Equal("grant_not_pending", Assert.Throws<GrantException>(() => grants.Decide(sam, pending.Id, true)).Code);
        Assert.Equal("grant_unknown", Assert.Throws<GrantException>(() => grants.Revoke(Guid.NewGuid(), pending.Id)).Code);
    }
}
