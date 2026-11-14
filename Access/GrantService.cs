using Dapper;
using EZEHR.Data;
using EZEHR.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EZEHR.Access;

public sealed class GrantException(string code) : Exception(code)
{
    /// <summary>offer_used, offer_expired, offer_unknown, grant_unknown or grant_not_pending.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// The patient's permissions. Sharing: the patient makes a single-use code, a clinician redeems it (Pending),
/// the patient approves the named clinician (Active until the chosen time). Apps get ongoing LabsAndNotes grants.
/// </summary>
public sealed class GrantService(SqliteConnection db, AuditLog audit, TimeProvider time, IOptions<AppOptions> options)
{
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(10);
    public const string TreatmentPurpose = "treatment";

    public ShareOffer CreateOffer(Guid patientId, ShareDuration duration)
    {
        var offer = new ShareOffer(Guid.NewGuid(), patientId, Codes.New(), duration, time.GetUtcNow() + OfferLifetime, null);
        db.Execute("INSERT INTO share_offers(id, patient_id, code, duration, offer_expires_at) VALUES (@id, @patient, @code, @duration, @expires)",
            new { id = offer.Id.ToString(), patient = patientId.ToString(), code = offer.Code, duration = duration.ToString(), expires = Db.Time(offer.OfferExpiresAt) });
        return offer;
    }

    public ShareOffer? FindOffer(Guid offerId) =>
        db.QuerySingleOrDefault<OfferRow>(OfferSelect + " WHERE id = @id", new { id = offerId.ToString() })?.ToOffer();

    /// <summary>A clinician enters the patient's code. Creates one Pending grant; a code works once and for 10 minutes.</summary>
    public Grant Redeem(string code, Party clinician)
    {
        code = Codes.Normalize(code);
        var now = time.GetUtcNow();
        Grant grant;
        using (var tx = db.BeginTransaction())
        {
            var offer = db.QuerySingleOrDefault<OfferRow>(OfferSelect + " WHERE code = @code", new { code }, tx)?.ToOffer()
                        ?? throw new GrantException("offer_unknown");
            if (offer.RedeemedGrantId is not null) throw new GrantException("offer_used");
            if (now > offer.OfferExpiresAt) throw new GrantException("offer_expired");

            grant = new Grant(Guid.NewGuid(), offer.PatientId, clinician, GrantScope.Full, TreatmentPurpose, GrantStatus.Pending,
                Minutes(offer.Duration), now, null, null, null);
            var claimed = db.Execute("UPDATE share_offers SET redeemed_grant_id = @grant WHERE id = @id AND redeemed_grant_id IS NULL",
                new { grant = grant.Id.ToString(), id = offer.Id.ToString() }, tx);
            if (claimed != 1) throw new GrantException("offer_used");
            Insert(grant, tx);
            tx.Commit();
        }
        audit.Append(grant.PatientId, clinician, "RedeemOffer", TreatmentPurpose, true, null);
        return grant;
    }

    /// <summary>The patient approves or denies a pending request. Approval starts the clock for the chosen duration.</summary>
    public Grant Decide(Guid patientId, Guid grantId, bool approve)
    {
        var grant = Owned(patientId, grantId);
        if (grant.Status != GrantStatus.Pending) throw new GrantException("grant_not_pending");
        var now = time.GetUtcNow();
        if (!approve)
        {
            db.Execute("UPDATE grants SET status = 'Denied' WHERE id = @id", new { id = grantId.ToString() });
            return grant with { Status = GrantStatus.Denied };
        }

        var duration = db.QuerySingleOrDefault<string>("SELECT duration FROM share_offers WHERE redeemed_grant_id = @id", new { id = grantId.ToString() });
        var expires = ExpiresAt(duration is null ? ShareDuration.OneHour : Enum.Parse<ShareDuration>(duration), now);
        db.Execute("UPDATE grants SET status = 'Active', approved_at = @now, expires_at = @expires WHERE id = @id",
            new { id = grantId.ToString(), now = Db.Time(now), expires = Db.Time(expires) });
        return grant with { Status = GrantStatus.Active, ApprovedAt = now, ExpiresAt = expires };
    }

    /// <summary>Ends a pending or active grant now.</summary>
    public Grant Revoke(Guid patientId, Guid grantId)
    {
        var grant = Owned(patientId, grantId);
        if (grant.Status is not (GrantStatus.Active or GrantStatus.Pending)) return grant;
        var now = time.GetUtcNow();
        db.Execute("UPDATE grants SET status = 'Revoked', revoked_at = @now WHERE id = @id", new { id = grantId.ToString(), now = Db.Time(now) });
        return grant with { Status = GrantStatus.Revoked, RevokedAt = now };
    }

    /// <summary>An app the patient turned on: lab results and notes only, ongoing until revoked. Idempotent.</summary>
    public Grant CreateAppGrant(Guid patientId, Party app)
    {
        var existing = ForGrantee(patientId, app.SubjectUri).FirstOrDefault(g => g.Status == GrantStatus.Active);
        if (existing is not null) return existing;
        var now = time.GetUtcNow();
        var grant = new Grant(Guid.NewGuid(), patientId, app, GrantScope.LabsAndNotes, $"{app.DisplayName} app", GrantStatus.Active, null, now, now, null, null);
        Insert(grant, null);
        return grant;
    }

    /// <summary>Marks a grant Expired (the gate calls this when it finds one past its time).</summary>
    public void MarkExpired(Guid grantId) =>
        db.Execute("UPDATE grants SET status = 'Expired' WHERE id = @id AND status = 'Active'", new { id = grantId.ToString() });

    public IReadOnlyList<Grant> ForPatient(Guid patientId) =>
        db.Query<GrantRow>(GrantSelect + " WHERE patient_id = @patient ORDER BY created_at DESC", new { patient = patientId.ToString() })
          .Select(r => r.ToGrant()).ToList();

    /// <summary>One grantee's grants for a patient, newest first.</summary>
    public IReadOnlyList<Grant> ForGrantee(Guid patientId, string subjectUri) =>
        db.Query<GrantRow>(GrantSelect + " WHERE patient_id = @patient AND grantee_uri = @subjectUri ORDER BY created_at DESC, rowid DESC",
                new { patient = patientId.ToString(), subjectUri })
          .Select(r => r.ToGrant()).ToList();

    public Grant? Find(Guid grantId) =>
        db.QuerySingleOrDefault<GrantRow>(GrantSelect + " WHERE id = @id", new { id = grantId.ToString() })?.ToGrant();

    /// <summary>When a grant approved at <paramref name="now"/> ends. End of day is 23:59:59 in AppOptions.TimeZone.</summary>
    public DateTimeOffset ExpiresAt(ShareDuration duration, DateTimeOffset now)
    {
        if (Minutes(duration) is { } minutes) return now.AddMinutes(minutes);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var endLocal = new DateTime(local.Year, local.Month, local.Day, 23, 59, 59, DateTimeKind.Unspecified);
        return new DateTimeOffset(endLocal, tz.GetUtcOffset(endLocal)).ToUniversalTime();
    }

    private static int? Minutes(ShareDuration d) => d switch
    {
        ShareDuration.FiveMinutes => 5,
        ShareDuration.OneHour => 60,
        ShareDuration.FourHours => 240,
        _ => null,
    };

    private Grant Owned(Guid patientId, Guid grantId)
    {
        var grant = Find(grantId);
        if (grant is null || grant.PatientId != patientId) throw new GrantException("grant_unknown");
        return grant;
    }

    private void Insert(Grant g, SqliteTransaction? tx) => db.Execute("""
        INSERT INTO grants(id, patient_id, grantee_kind, grantee_uri, grantee_name, grantee_org, scope, purpose, status,
                           duration_minutes, created_at, approved_at, expires_at, revoked_at)
        VALUES (@id, @patient, @kind, @uri, @name, @org, @scope, @purpose, @status, @minutes, @created, @approved, @expires, NULL)
        """,
        new
        {
            id = g.Id.ToString(), patient = g.PatientId.ToString(),
            kind = g.Grantee.Kind.ToString(), uri = g.Grantee.SubjectUri, name = g.Grantee.DisplayName, org = g.Grantee.OrgName,
            scope = g.Scope.ToString(), purpose = g.Purpose, status = g.Status.ToString(), minutes = g.DurationMinutes,
            created = Db.Time(g.CreatedAt), approved = g.ApprovedAt is { } a ? Db.Time(a) : null, expires = g.ExpiresAt is { } e ? Db.Time(e) : null,
        }, tx);

    private const string OfferSelect = "SELECT id, patient_id AS PatientId, code, duration, offer_expires_at AS OfferExpiresAt, redeemed_grant_id AS RedeemedGrantId FROM share_offers";

    private const string GrantSelect = """
        SELECT id, patient_id AS PatientId, grantee_kind AS GranteeKind, grantee_uri AS GranteeUri, grantee_name AS GranteeName,
               grantee_org AS GranteeOrg, scope, purpose, status, duration_minutes AS DurationMinutes, created_at AS CreatedAt,
               approved_at AS ApprovedAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt
        FROM grants
        """;

    private sealed class OfferRow
    {
        public string Id { get; set; } = "";
        public string PatientId { get; set; } = "";
        public string Code { get; set; } = "";
        public string Duration { get; set; } = "";
        public string OfferExpiresAt { get; set; } = "";
        public string? RedeemedGrantId { get; set; }

        public ShareOffer ToOffer() => new(Guid.Parse(Id), Guid.Parse(PatientId), Code, Enum.Parse<ShareDuration>(Duration),
            Db.Time(OfferExpiresAt), RedeemedGrantId is null ? null : Guid.Parse(RedeemedGrantId));
    }

    private sealed class GrantRow
    {
        public string Id { get; set; } = "";
        public string PatientId { get; set; } = "";
        public string GranteeKind { get; set; } = "";
        public string GranteeUri { get; set; } = "";
        public string GranteeName { get; set; } = "";
        public string GranteeOrg { get; set; } = "";
        public string Scope { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Status { get; set; } = "";
        public long? DurationMinutes { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? ApprovedAt { get; set; }
        public string? ExpiresAt { get; set; }
        public string? RevokedAt { get; set; }

        public Grant ToGrant() => new(Guid.Parse(Id), Guid.Parse(PatientId),
            new Party(Enum.Parse<PartyKind>(GranteeKind), GranteeUri, GranteeName, GranteeOrg),
            Enum.Parse<GrantScope>(Scope), Purpose, Enum.Parse<GrantStatus>(Status), (int?)DurationMinutes,
            Db.Time(CreatedAt), Db.TimeOrNull(ApprovedAt), Db.TimeOrNull(ExpiresAt), Db.TimeOrNull(RevokedAt));
    }
}
