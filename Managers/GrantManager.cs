using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
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
    public static readonly TimeSpan ShareOfferLifetime = TimeSpan.FromMinutes(10);

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
    public static string GenerateCode()
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

    public async Task<ShareOffer> CreateShareOfferAsync(Guid patientId, ShareDuration duration)
    {
        ShareOffer offer = new(
            Guid.NewGuid(),
            patientId,
            GenerateCode(),
            duration,
            time.GetUtcNow() + ShareOfferLifetime,
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

    public async Task<ShareOffer?> FindShareOfferAsync(Guid offerId)
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

    /// <summary>The patient's own offer and the request made with it, if any. Null for someone else's offer.</summary>
    public async Task<ShareStatus?> ReadShareStatusAsync(Guid patientId, Guid offerId)
    {
        ShareOffer? offer = await FindShareOfferAsync(offerId);
        if (offer is null || offer.PatientId != patientId)
        {
            return null;
        }
        Grant? grant = offer.RedeemedGrantId is Guid grantId ? await FindAsync(grantId) : null;
        DateTimeOffset now = time.GetUtcNow();
        string? untilIfApprovedNow = grant is { Status: GrantStatus.Pending }
            ? LocalTime.Clock(CalculateExpiresAt(grant.DurationMinutes, now), _timeZone)
            : null;
        string? until = grant?.ExpiresAt is DateTimeOffset expiresAt
            ? LocalTime.Clock(expiresAt, _timeZone)
            : null;
        string formattedCode = FormatCode(offer.Code);
        return new ShareStatus(
            offer.Id,
            formattedCode,
            ShareCodeQr.PngDataUri(formattedCode),
            grant is null && offer.OfferExpiresAt < now,
            grant,
            untilIfApprovedNow,
            until
        );
    }

    /// <summary>Everyone who has or had access, newest first, as the Access page lists them.</summary>
    public async Task<IReadOnlyList<AccessLine>> ListAccessAsync(Guid patientId)
    {
        DateTimeOffset now = time.GetUtcNow();
        IReadOnlyList<Grant> grants = await ListForPatientAsync(patientId);
        return grants
            .OrderByDescending(grant => grant.CreatedAt)
            .Select(grant => ToAccessLine(grant, now))
            .ToList();
    }

    private AccessLine ToAccessLine(Grant grant, DateTimeOffset now)
    {
        Party grantee = grant.Grantee;
        bool expired = grant.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= now;
        if (grant.Status == GrantStatus.Active && !expired)
        {
            string text =
                grantee.Kind == PartyKind.App
                    ? Copy.AppGrantSummary(
                        grantee.DisplayName,
                        Copy.AppGrantWhat,
                        LocalTime.Day(grant.ApprovedAt ?? grant.CreatedAt, _timeZone)
                    )
                    : Copy.ClinicianAccessUntil(
                        grantee.DisplayName,
                        grantee.OrgName,
                        LocalTime.Clock(grant.ExpiresAt!.Value, _timeZone)
                    );
            return new AccessLine(grant.Id, text, true);
        }
        if (grant.Status == GrantStatus.Pending)
        {
            return new AccessLine(
                grant.Id,
                Copy.ClinicianAccessPending(grantee.DisplayName, grantee.OrgName),
                true
            );
        }
        GrantStatus ended = expired ? GrantStatus.Expired : grant.Status;
        return new AccessLine(
            grant.Id,
            Copy.AccessEndedLine(
                grantee.Kind == PartyKind.App
                    ? grantee.DisplayName
                    : $"{grantee.DisplayName}, {grantee.OrgName}",
                ended
            ),
            false
        );
    }

    /// <summary>A clinician enters the patient's code. Creates one Pending grant; a code works once and for 10 minutes.</summary>
    public async Task<GrantResult> RedeemShareCodeAsync(string code, Party clinician)
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
            ToDurationMinutes(claimed.Duration),
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
            "RedeemShareCode",
            TreatmentPurpose,
            true,
            null
        );
        await transaction.CommitAsync();
        return Succeeded(grant);
    }

    /// <summary>The patient approves or denies a pending request. Approval starts the clock for the chosen duration.</summary>
    public async Task<GrantResult> ApproveOrDenyAsync(Guid patientId, Guid grantId, bool approve)
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
                ExpiresAt = CalculateExpiresAt(grant.DurationMinutes, now),
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
        IReadOnlyList<Grant> existing = await ListForGranteeAsync(patientId, app.SubjectUri);
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

    public async Task<IReadOnlyList<Grant>> ListForPatientAsync(Guid patientId)
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
    public async Task<IReadOnlyList<Grant>> ListForGranteeAsync(Guid patientId, string subjectUri)
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

    /// <summary>The grantee's most recent grant for a patient: what a waiting clinician's request became.</summary>
    public async Task<Grant?> FindNewestForGranteeAsync(Guid patientId, Party grantee)
    {
        IReadOnlyList<Grant> grants = await ListForGranteeAsync(patientId, grantee.SubjectUri);
        return grants.FirstOrDefault();
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
    public DateTimeOffset CalculateExpiresAt(int? durationMinutes, DateTimeOffset now)
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

    private static int? ToDurationMinutes(ShareDuration duration)
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
