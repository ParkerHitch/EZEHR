using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>
/// The patient's permissions. Sharing: the patient makes a single-use code, a clinician redeems it (Pending),
/// the patient approves the named clinician (Active until the chosen time). Apps get ongoing LabsAndNotes grants.
/// Also the one place that makes, normalizes and formats share and invite codes.
/// </summary>
public sealed class GrantManager(
    Db db,
    AuditManager auditManager,
    TimeProvider time,
    IOptions<AppOptions> options
)
{
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(10);

    public const string TreatmentPurpose = "treatment";

    // 8 characters with no look-alikes (no I, L, O, U, 0, 1).
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    private const string GrantSelect = """
        SELECT id AS Id, patient_id AS PatientId, grantee_kind AS GranteeKind,
               grantee_uri AS GranteeUri, grantee_name AS GranteeName, grantee_org AS GranteeOrg,
               scope AS Scope, purpose AS Purpose, status AS Status,
               duration_minutes AS DurationMinutes, created_at AS CreatedAt,
               approved_at AS ApprovedAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt
        FROM grants
        """;

    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(
        options.Value.TimeZone
    );

    /// <summary>A new opaque code for a share offer or an invite.</summary>
    public static string NewCode()
    {
        return new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, 8));
    }

    /// <summary>Accepts "abcd-efgh", "ABCD EFGH" and so on. Codes are stored and looked up in this form.</summary>
    public static string NormalizeCode(string input)
    {
        return new string(
            input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()
        );
    }

    /// <summary>XXXX-XXXX, the way a patient reads a code aloud.</summary>
    public static string FormatCode(string code)
    {
        if (code.Length != 8)
        {
            return code;
        }
        return $"{code[..4]}-{code[4..]}";
    }

    public async Task<ShareOffer> CreateOfferAsync(Guid patientId, ShareDuration duration)
    {
        ShareOffer offer = new(
            Guid.NewGuid(),
            patientId,
            NewCode(),
            duration,
            time.GetUtcNow() + OfferLifetime,
            null
        );
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO share_offers(id, patient_id, code, duration, offer_expires_at)
            VALUES (@Id, @PatientId, @Code, @duration, @OfferExpiresAt)
            """,
            new
            {
                offer.Id,
                offer.PatientId,
                offer.Code,
                duration = duration.ToString(),
                offer.OfferExpiresAt,
            }
        );
        return offer;
    }

    public async Task<ShareOffer?> FindOfferAsync(Guid offerId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<ShareOffer>(
            """
            SELECT id AS Id, patient_id AS PatientId, code AS Code, duration AS Duration,
                   offer_expires_at AS OfferExpiresAt, redeemed_grant_id AS RedeemedGrantId
            FROM share_offers
            WHERE id = @offerId
            """,
            new { offerId }
        );
    }

    /// <summary>A clinician enters the patient's code. Creates one Pending grant; a code works once and for 10 minutes.</summary>
    public async Task<GrantResult> RedeemAsync(string code, Party clinician)
    {
        string normalizedCode = NormalizeCode(code);
        DateTimeOffset now = time.GetUtcNow();
        Guid grantId = Guid.NewGuid();
        await using SqlConnection connection = await db.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();

        // The conditional UPDATE is the claim: of two redeems racing for one code, only one changes the row.
        ClaimedOffer? claimed = await connection.QuerySingleOrDefaultAsync<ClaimedOffer>(
            """
            UPDATE share_offers SET redeemed_grant_id = @grantId
            OUTPUT inserted.patient_id AS PatientId, inserted.duration AS Duration
            WHERE code = @normalizedCode AND redeemed_grant_id IS NULL AND offer_expires_at >= @now
            """,
            new
            {
                grantId,
                normalizedCode,
                now,
            },
            transaction
        );
        if (claimed is null)
        {
            ShareOffer? offer = await connection.QuerySingleOrDefaultAsync<ShareOffer>(
                """
                SELECT id AS Id, patient_id AS PatientId, code AS Code, duration AS Duration,
                       offer_expires_at AS OfferExpiresAt, redeemed_grant_id AS RedeemedGrantId
                FROM share_offers
                WHERE code = @normalizedCode
                """,
                new { normalizedCode },
                transaction
            );
            if (offer is null)
            {
                return Failed(GrantError.OfferUnknown);
            }
            if (offer.RedeemedGrantId is not null)
            {
                return Failed(GrantError.OfferUsed);
            }
            return Failed(GrantError.OfferExpired);
        }

        Grant grant = new(
            grantId,
            claimed.PatientId,
            clinician,
            GrantScope.Full,
            TreatmentPurpose,
            GrantStatus.Pending,
            Minutes(claimed.Duration),
            now,
            null,
            null,
            null
        );
        await InsertAsync(connection, transaction, grant);
        await auditManager.AppendAsync(
            connection,
            transaction,
            grant.PatientId,
            clinician,
            "RedeemOffer",
            TreatmentPurpose,
            true,
            null
        );
        await transaction.CommitAsync();
        return Succeeded(grant);
    }

    /// <summary>The patient approves or denies a pending request. Approval starts the clock for the chosen duration.</summary>
    public async Task<GrantResult> DecideAsync(Guid patientId, Guid grantId, bool approve)
    {
        Grant? grant = await FindAsync(grantId);
        if (grant is null || grant.PatientId != patientId)
        {
            return Failed(GrantError.GrantUnknown);
        }
        if (grant.Status != GrantStatus.Pending)
        {
            return Failed(GrantError.GrantNotPending);
        }

        DateTimeOffset now = time.GetUtcNow();
        Grant decided = approve
            ? grant with
            {
                Status = GrantStatus.Active,
                ApprovedAt = now,
                ExpiresAt = ExpiresAt(grant.DurationMinutes, now),
            }
            : grant with
            {
                Status = GrantStatus.Denied,
            };
        await using SqlConnection connection = await db.OpenAsync();
        int changed = await connection.ExecuteAsync(
            """
            UPDATE grants SET status = @status, approved_at = @ApprovedAt, expires_at = @ExpiresAt
            WHERE id = @Id AND status = 'Pending'
            """,
            new
            {
                decided.Id,
                status = decided.Status.ToString(),
                decided.ApprovedAt,
                decided.ExpiresAt,
            }
        );
        if (changed != 1)
        {
            return Failed(GrantError.GrantNotPending);
        }
        return Succeeded(decided);
    }

    /// <summary>Ends a pending or active grant now. A grant that has already ended is returned as it is.</summary>
    public async Task<GrantResult> RevokeAsync(Guid patientId, Guid grantId)
    {
        Grant? grant = await FindAsync(grantId);
        if (grant is null || grant.PatientId != patientId)
        {
            return Failed(GrantError.GrantUnknown);
        }
        if (grant.Status is not (GrantStatus.Active or GrantStatus.Pending))
        {
            return Succeeded(grant);
        }

        DateTimeOffset now = time.GetUtcNow();
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            UPDATE grants SET status = 'Revoked', revoked_at = @now
            WHERE id = @grantId AND status IN ('Active', 'Pending')
            """,
            new { grantId, now }
        );
        return Succeeded(grant with { Status = GrantStatus.Revoked, RevokedAt = now });
    }

    /// <summary>An app the patient turned on: lab results and notes only, ongoing until revoked. Idempotent.</summary>
    public async Task<Grant> CreateAppGrantAsync(Guid patientId, Party app)
    {
        IReadOnlyList<Grant> existing = await ForGranteeAsync(patientId, app.SubjectUri);
        Grant? active = existing.FirstOrDefault(grant => grant.Status == GrantStatus.Active);
        if (active is not null)
        {
            return active;
        }

        DateTimeOffset now = time.GetUtcNow();
        Grant grant = new(
            Guid.NewGuid(),
            patientId,
            app,
            GrantScope.LabsAndNotes,
            $"{app.DisplayName} app",
            GrantStatus.Active,
            null,
            now,
            now,
            null,
            null
        );
        await using SqlConnection connection = await db.OpenAsync();
        await InsertAsync(connection, null, grant);
        return grant;
    }

    /// <summary>Marks a grant Expired (AccessManager calls this when it finds one past its time).</summary>
    public async Task MarkExpiredAsync(Guid grantId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE grants SET status = 'Expired' WHERE id = @grantId AND status = 'Active'",
            new { grantId }
        );
    }

    public async Task<IReadOnlyList<Grant>> ForPatientAsync(Guid patientId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        IEnumerable<GrantRow> rows = await connection.QueryAsync<GrantRow>(
            $"""
            {GrantSelect}
            WHERE patient_id = @patientId
            ORDER BY created_at DESC, id DESC
            """,
            new { patientId }
        );
        return rows.Select(row => row.ToGrant()).ToList();
    }

    /// <summary>One grantee's grants for a patient, newest first.</summary>
    public async Task<IReadOnlyList<Grant>> ForGranteeAsync(Guid patientId, string subjectUri)
    {
        await using SqlConnection connection = await db.OpenAsync();
        IEnumerable<GrantRow> rows = await connection.QueryAsync<GrantRow>(
            $"""
            {GrantSelect}
            WHERE patient_id = @patientId AND grantee_uri = @subjectUri
            ORDER BY created_at DESC, id DESC
            """,
            new { patientId, subjectUri }
        );
        return rows.Select(row => row.ToGrant()).ToList();
    }

    public async Task<Grant?> FindAsync(Guid grantId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        GrantRow? row = await connection.QuerySingleOrDefaultAsync<GrantRow>(
            $"""
            {GrantSelect}
            WHERE id = @grantId
            """,
            new { grantId }
        );
        return row?.ToGrant();
    }

    /// <summary>When a grant approved at <paramref name="now"/> ends. No minutes means end of day: 23:59:59 in AppOptions.TimeZone.</summary>
    public DateTimeOffset ExpiresAt(int? durationMinutes, DateTimeOffset now)
    {
        if (durationMinutes is int minutes)
        {
            return now.AddMinutes(minutes);
        }
        DateTimeOffset local = TimeZoneInfo.ConvertTime(now, _timeZone);
        DateTime endLocal = new(
            local.Year,
            local.Month,
            local.Day,
            23,
            59,
            59,
            DateTimeKind.Unspecified
        );
        return new DateTimeOffset(endLocal, _timeZone.GetUtcOffset(endLocal)).ToUniversalTime();
    }

    private static int? Minutes(ShareDuration duration)
    {
        return duration switch
        {
            ShareDuration.FiveMinutes => 5,
            ShareDuration.OneHour => 60,
            ShareDuration.FourHours => 240,
            _ => null,
        };
    }

    private static GrantResult Succeeded(Grant grant)
    {
        return new GrantResult(true, grant, null);
    }

    private static GrantResult Failed(GrantError error)
    {
        return new GrantResult(false, null, error);
    }

    private static async Task InsertAsync(
        SqlConnection connection,
        DbTransaction? transaction,
        Grant grant
    )
    {
        await connection.ExecuteAsync(
            """
            INSERT INTO grants(id, patient_id, grantee_kind, grantee_uri, grantee_name, grantee_org,
                               scope, purpose, status, duration_minutes, created_at, approved_at,
                               expires_at, revoked_at)
            VALUES (@Id, @PatientId, @kind, @uri, @name, @org, @scope, @Purpose, @status,
                    @DurationMinutes, @CreatedAt, @ApprovedAt, @ExpiresAt, NULL)
            """,
            new
            {
                grant.Id,
                grant.PatientId,
                kind = grant.Grantee.Kind.ToString(),
                uri = grant.Grantee.SubjectUri,
                name = grant.Grantee.DisplayName,
                org = grant.Grantee.OrgName,
                scope = grant.Scope.ToString(),
                grant.Purpose,
                status = grant.Status.ToString(),
                grant.DurationMinutes,
                grant.CreatedAt,
                grant.ApprovedAt,
                grant.ExpiresAt,
            },
            transaction
        );
    }

    private sealed record ClaimedOffer(Guid PatientId, ShareDuration Duration);

    private sealed record GrantRow(
        Guid Id,
        Guid PatientId,
        PartyKind GranteeKind,
        string GranteeUri,
        string GranteeName,
        string GranteeOrg,
        GrantScope Scope,
        string Purpose,
        GrantStatus Status,
        int? DurationMinutes,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ApprovedAt,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset? RevokedAt
    )
    {
        public Grant ToGrant()
        {
            return new Grant(
                Id,
                PatientId,
                new Party(GranteeKind, GranteeUri, GranteeName, GranteeOrg),
                Scope,
                Purpose,
                Status,
                DurationMinutes,
                CreatedAt,
                ApprovedAt,
                ExpiresAt,
                RevokedAt
            );
        }
    }
}
