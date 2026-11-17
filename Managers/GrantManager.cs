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
/// Emergency access: a patient who turned it on has an emergency code, and a clinician who enters it with a reason
/// gets an Active grant for one hour with no approval. Either way the clinician needs an open launch from their
/// own organization's system (LaunchManager), and the grant carries that launch's visit.
/// Also the one place that makes, normalizes and formats share and invite codes.
/// </summary>
public sealed class GrantManager(
    Db db,
    AuditManager auditManager,
    InviteManager inviteManager,
    OrganizationManager organizationManager,
    LaunchManager launchManager,
    TimeProvider time,
    IOptions<AppOptions> options
)
{
    public static readonly TimeSpan ShareOfferLifetime = TimeSpan.FromMinutes(10);

    public const string TreatmentPurpose = "treatment";

    /// <summary>How long emergency access lasts. Fixed: the clinician doesn't choose it and the patient didn't.</summary>
    public static readonly TimeSpan EmergencyAccessLifetime = TimeSpan.FromHours(1);

    /// <summary>The purpose on an emergency grant, and the claimed purpose logged with each read made under one.</summary>
    public const string EmergencyPurpose = "emergency";

    /// <summary>The audit action for opening emergency access. Its claimed purpose is the clinician's stated reason.</summary>
    public const string OpenEmergencyAccessAction = "OpenEmergencyAccess";

    /// <summary>Longest reason kept, so it fits the grant and audit columns.</summary>
    public const int MaxEmergencyReasonLength = 200;

    // 8 characters with no look-alikes (no I, L, O, U, 0, 1).
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    private const string GrantSelect = """
        SELECT id AS Id, patient_id AS PatientId, grantee_kind AS GranteeKind,
               grantee_uri AS GranteeUri, grantee_name AS GranteeName, grantee_org AS GranteeOrg,
               grantee_org_uri AS GranteeOrgUri, scope AS Scope, purpose AS Purpose, status AS Status,
               duration_minutes AS DurationMinutes, created_at AS CreatedAt,
               approved_at AS ApprovedAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt,
               emergency_reason AS EmergencyReason, encounter_id AS EncounterId
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

    /// <summary>
    /// Accepts "abcd-efgh", "ABCD EFGH" and so on. Codes are stored and looked up in this form. A form field left
    /// empty arrives as null, and normalizes to "", which matches no code.
    /// </summary>
    public static string NormalizeCode(string? input)
    {
        return new string(
            (input ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()
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

    /// <summary>
    /// Everyone who has or had access, newest first, the providers that can add records, and every app on the
    /// network, as the Access page lists them. A listed app's current grant comes out of the first list, since the
    /// page shows it under Apps. A grant for an app that is no longer listed stays in the first list, so the patient
    /// can still see and revoke it.
    /// </summary>
    public async Task<AccessOverview> ListAccessAsync(Guid patientId)
    {
        DateTimeOffset now = time.GetUtcNow();
        IReadOnlyList<Grant> grants = await ListForPatientAsync(patientId);
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        IReadOnlyList<Organization> apps = directory.ListApps();
        List<AccessLine> lines = [];
        Dictionary<string, AccessLine> currentAppGrants = [];
        foreach (Grant grant in grants.OrderByDescending(grant => grant.CreatedAt))
        {
            AccessLine line = ToAccessLine(grant, now);
            bool listedApp = apps.Any(app => app.Uri == grant.Grantee.SubjectUri);
            if (listedApp && line.CanRevoke)
            {
                currentAppGrants.TryAdd(grant.Grantee.SubjectUri, line);
            }
            else
            {
                lines.Add(line);
            }
        }
        List<AppLine> appLines = apps.Select(app => new AppLine(
                app.Uri,
                app.DisplayName,
                app.AppDescription,
                Copy.AppScopeWhat(app.GrantedAppScope()),
                currentAppGrants.GetValueOrDefault(app.Uri)
            ))
            .ToList();
        IReadOnlyList<ConnectedProviderLine> connectedProviders =
            await inviteManager.ListConnectedProvidersAsync(patientId);
        await using SqlConnection connection = await db.OpenAsync();
        string? emergencyCode = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT emergency_code FROM patients WHERE id = @patientId",
            new { patientId }
        );
        return new AccessOverview(
            lines,
            connectedProviders,
            appLines,
            emergencyCode is null ? null : FormatCode(emergencyCode)
        );
    }

    private AccessLine ToAccessLine(Grant grant, DateTimeOffset now)
    {
        Party grantee = grant.Grantee;
        bool expired = grant.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= now;
        if (grant.Status == GrantStatus.Active && !expired && grant.EmergencyReason is not null)
        {
            return new AccessLine(
                grant.Id,
                Copy.EmergencyAccessUntil(
                    grantee.DisplayName,
                    grantee.OrgName,
                    LocalTime.Clock(grant.ExpiresAt!.Value, _timeZone),
                    grant.EmergencyReason
                ),
                true,
                true
            );
        }
        if (grant.Status == GrantStatus.Active && !expired)
        {
            string text =
                grantee.Kind == PartyKind.App
                    ? Copy.AppGrantSummary(
                        grantee.DisplayName,
                        Copy.AppScopeWhat(grant.Scope),
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
        string who =
            grantee.Kind == PartyKind.App
                ? grantee.DisplayName
                : $"{grantee.DisplayName}, {grantee.OrgName}";
        return new AccessLine(
            grant.Id,
            Copy.AccessEndedLine(
                grant.EmergencyReason is null ? who : Copy.EmergencyAccessWho(who),
                ended
            ),
            false
        );
    }

    /// <summary>
    /// Turns emergency access on with a new emergency code, or replaces the code when it's already on (the old one
    /// stops working). Returns the code as stored.
    /// </summary>
    public async Task<string> TurnOnEmergencyAccessAsync(Guid patientId)
    {
        string emergencyCode = GenerateCode();
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE patients SET emergency_code = @emergencyCode WHERE id = @patientId",
            new { emergencyCode, patientId }
        );
        return emergencyCode;
    }

    /// <summary>Removes the emergency code, so nobody can open new emergency access. Access already open runs out or is revoked as usual.</summary>
    public async Task TurnOffEmergencyAccessAsync(Guid patientId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE patients SET emergency_code = NULL WHERE id = @patientId",
            new { patientId }
        );
    }

    /// <summary>
    /// Break-glass: a verified clinician enters the patient's emergency code and a reason, and gets an Active grant
    /// for one hour with no approval step. The patient consented ahead of time by turning emergency access on. The
    /// grant and its audit event, which carries the reason, commit together.
    /// </summary>
    public async Task<GrantResult> OpenEmergencyAccessAsync(
        string emergencyCode,
        string? reason,
        Party clinician,
        Guid launchId
    )
    {
        // The organization has to be part of an emergency too. The launch can't be matched to the patient, who may
        // never have been to this organization, so it vouches only for the clinician and the visit.
        ClinicianLaunch? launch = await launchManager.FindOpenLaunchAsync(launchId, clinician);
        if (launch is null)
        {
            return Failed(GrantError.LaunchNotOpen);
        }
        string statedReason = (reason ?? "").Trim();
        if (statedReason.Length == 0)
        {
            return Failed(GrantError.EmergencyReasonMissing);
        }
        if (statedReason.Length > MaxEmergencyReasonLength)
        {
            statedReason = statedReason[..MaxEmergencyReasonLength];
        }
        string normalizedCode = NormalizeCode(emergencyCode);
        DateTimeOffset now = time.GetUtcNow();
        await using SqlConnection connection = await db.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        Guid? patientId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM patients WHERE emergency_code = @normalizedCode",
            new { normalizedCode },
            transaction
        );
        if (patientId is not Guid foundPatientId)
        {
            return Failed(GrantError.EmergencyCodeUnknown);
        }

        Grant grant = new(
            Guid.NewGuid(),
            foundPatientId,
            clinician,
            GrantScope.Full,
            EmergencyPurpose,
            GrantStatus.Active,
            (int)EmergencyAccessLifetime.TotalMinutes,
            now,
            null,
            now + EmergencyAccessLifetime,
            null,
            statedReason,
            launch.EncounterId
        );
        await InsertAsync(connection, transaction, grant);
        await auditManager.AppendAsync(
            connection,
            transaction,
            foundPatientId,
            clinician,
            OpenEmergencyAccessAction,
            statedReason,
            true,
            null,
            launch.EncounterId
        );
        await transaction.CommitAsync();
        return Succeeded(grant);
    }

    /// <summary>
    /// A clinician enters the patient's code. Creates one Pending grant; a code works once and for 10 minutes.
    /// The clinician needs an open launch from their own organization, and the code must be the code of the patient
    /// whose chart that launch came from: the patient has to be linked to that organization under the launch's
    /// patient id. A code for anyone else is refused, stays unused, and is logged to that patient as blocked.
    /// </summary>
    public async Task<GrantResult> RedeemShareCodeAsync(
        string? code,
        Party clinician,
        Guid launchId
    )
    {
        ClinicianLaunch? launch = await launchManager.FindOpenLaunchAsync(launchId, clinician);
        if (launch is null)
        {
            return Failed(GrantError.LaunchNotOpen);
        }
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

        bool launchedForThisPatient = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM provider_links
                WHERE patient_id = @PatientId AND provider_uri = @ProviderUri
                      AND provider_patient_id = @ProviderPatientId) THEN 1 ELSE 0 END
            """,
            new
            {
                claimed.PatientId,
                launch.ProviderUri,
                launch.ProviderPatientId,
            },
            transaction
        );
        if (!launchedForThisPatient)
        {
            // Rolling back gives the code back to the patient, unused.
            await transaction.RollbackAsync();
            await auditManager.AppendAsync(
                claimed.PatientId,
                clinician,
                "RedeemShareCode",
                TreatmentPurpose,
                false,
                DenyReason.LaunchPatientMismatch,
                launch.EncounterId
            );
            return Failed(GrantError.OfferNotForLaunchedPatient);
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
            null,
            null,
            launch.EncounterId
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
            null,
            launch.EncounterId
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

    /// <summary>
    /// The patient turns an app on, from the Access page (or Kidney Check at enrollment). The grant gets the scope
    /// the app declares, and nothing more. After a revoke this makes a new grant. Idempotent while one is active.
    /// Null when the URI isn't an app in the organizations table: an app a sysadmin removed is cut off, so nothing
    /// is granted.
    /// </summary>
    public async Task<Grant?> TurnOnAppAsync(Guid patientId, string appUri)
    {
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        Organization? app = directory.FindApp(appUri);
        if (app is null)
        {
            return null;
        }
        return await CreateAppGrantAsync(patientId, app.ToParty(), app.GrantedAppScope());
    }

    /// <summary>An app the patient turned on: its scope only, ongoing until revoked. Idempotent.</summary>
    public async Task<Grant> CreateAppGrantAsync(
        Guid patientId,
        Party app,
        GrantScope scope = GrantScope.LabsAndNotes
    )
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
            scope,
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
                               grantee_org_uri, scope, purpose, status, duration_minutes, created_at,
                               approved_at, expires_at, revoked_at, emergency_reason, encounter_id)
            VALUES (@Id, @PatientId, @kind, @uri, @name, @org, @orgUri, @scope, @Purpose, @status,
                    @DurationMinutes, @CreatedAt, @ApprovedAt, @ExpiresAt, NULL, @EmergencyReason,
                    @EncounterId)
            """,
            new
            {
                grant.Id,
                grant.PatientId,
                kind = grant.Grantee.Kind.ToString(),
                uri = grant.Grantee.SubjectUri,
                name = grant.Grantee.DisplayName,
                org = grant.Grantee.OrgName,
                orgUri = grant.Grantee.OrgUri,
                scope = grant.Scope.ToString(),
                grant.Purpose,
                status = grant.Status.ToString(),
                grant.DurationMinutes,
                grant.CreatedAt,
                grant.ApprovedAt,
                grant.ExpiresAt,
                grant.EmergencyReason,
                grant.EncounterId,
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
        string? GranteeOrgUri,
        GrantScope Scope,
        string Purpose,
        GrantStatus Status,
        int? DurationMinutes,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ApprovedAt,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset? RevokedAt,
        string? EmergencyReason,
        string? EncounterId
    )
    {
        public Grant ToGrant()
        {
            return new Grant(
                Id,
                PatientId,
                // Rows written before grantee_org_uri existed fall back to the grantee itself.
                new Party(
                    GranteeKind,
                    GranteeUri,
                    GranteeName,
                    GranteeOrg,
                    GranteeOrgUri ?? GranteeUri
                ),
                Scope,
                Purpose,
                Status,
                DurationMinutes,
                CreatedAt,
                ApprovedAt,
                ExpiresAt,
                RevokedAt,
                EmergencyReason,
                EncounterId
            );
        }
    }
}
