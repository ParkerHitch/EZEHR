using System.Data.Common;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>
/// Append-only log of every access attempt for a patient, granted or denied. The patient sees all of it. An attempt
/// on an id that isn't a patient is not logged, so callers can't fill the table by guessing ids. Each event carries
/// a fingerprint chained to the patient's previous event (AuditChain), so a change made behind the app's back shows.
/// </summary>
public sealed class AuditManager(Db db, TimeProvider time)
{
    /// <summary>Longest claimed purpose kept. The broker chooses it, so it's cut to fit the column.</summary>
    public const int MaxClaimedPurposeLength = 200;

    /// <summary>The event as stored, or null when no such patient exists and nothing was stored.</summary>
    public async Task<AuditEvent?> AppendAsync(
        Guid patientId,
        Party? actor,
        string action,
        string? claimedPurpose,
        bool granted,
        DenyReason? reason
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        return await AppendAsync(
            connection,
            null,
            patientId,
            actor,
            action,
            claimedPurpose,
            granted,
            reason
        );
    }

    /// <summary>Appends inside the caller's transaction, so the event commits or rolls back with the change it records.</summary>
    public async Task<AuditEvent?> AppendAsync(
        SqlConnection connection,
        DbTransaction? transaction,
        Guid patientId,
        Party? actor,
        string action,
        string? claimedPurpose,
        bool granted,
        DenyReason? reason
    )
    {
        DateTimeOffset at = time.GetUtcNow();
        string? purpose = claimedPurpose is { Length: > MaxClaimedPurposeLength }
            ? claimedPurpose[..MaxClaimedPurposeLength]
            : claimedPurpose;
        // Each event's fingerprint includes the one before it, so appends for one patient must run one at a time.
        // The update lock on the patient's row is held until the transaction ends; a caller without a transaction
        // gets one here.
        await using DbTransaction? ownTransaction = transaction is null
            ? await connection.BeginTransactionAsync()
            : null;
        DbTransaction activeTransaction = transaction ?? ownTransaction!;
        bool patientExists = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM patients WITH (UPDLOCK, ROWLOCK) WHERE id = @patientId)
                        THEN 1 ELSE 0 END
            """,
            new { patientId },
            activeTransaction
        );
        if (!patientExists)
        {
            return null;
        }
        string? previousHash = await connection.QueryFirstOrDefaultAsync<string?>(
            "SELECT TOP 1 chain_hash FROM audit_events WHERE patient_id = @patientId ORDER BY id DESC",
            new { patientId },
            activeTransaction
        );
        string chainHash = AuditChain.ComputeHash(
            previousHash,
            patientId,
            at,
            actor,
            action,
            purpose,
            granted,
            reason
        );
        long storedId = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO audit_events(patient_id, at, actor_kind, actor_uri, actor_name, actor_org,
                                     actor_org_uri, action, claimed_purpose, granted, reason, chain_hash)
            OUTPUT inserted.id
            VALUES (@patientId, @at, @kind, @uri, @name, @org, @orgUri, @action, @purpose, @granted,
                    @reason, @chainHash)
            """,
            new
            {
                patientId,
                at,
                kind = actor?.Kind.ToString(),
                uri = actor?.SubjectUri,
                name = actor?.DisplayName,
                org = actor?.OrgName,
                orgUri = actor?.OrgUri,
                action,
                purpose,
                granted,
                reason = reason?.ToString(),
                chainHash,
            },
            activeTransaction
        );
        if (ownTransaction is not null)
        {
            await ownTransaction.CommitAsync();
        }
        return new AuditEvent(
            storedId,
            patientId,
            at,
            actor,
            action,
            purpose,
            granted,
            reason,
            chainHash
        );
    }

    /// <summary>Events for one patient, oldest first, after the given id (for htmx polling).</summary>
    public async Task<IReadOnlyList<AuditEvent>> ListForPatientAsync(
        Guid patientId,
        long afterId = 0
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        IEnumerable<AuditRow> rows = await connection.QueryAsync<AuditRow>(
            """
            SELECT id AS Id, patient_id AS PatientId, at AS At, actor_kind AS ActorKind,
                   actor_uri AS ActorUri, actor_name AS ActorName, actor_org AS ActorOrg, actor_org_uri AS ActorOrgUri,
                   action AS Action, claimed_purpose AS ClaimedPurpose, granted AS Granted,
                   reason AS Reason, chain_hash AS ChainHash
            FROM audit_events
            WHERE patient_id = @patientId AND id > @afterId
            ORDER BY id
            """,
            new { patientId, afterId }
        );
        return rows.Select(row => row.ToAuditEvent()).ToList();
    }

    /// <summary>Recomputes the patient's whole chain from the stored events and says whether it still holds.</summary>
    public async Task<AuditChainCheck> VerifyChainAsync(Guid patientId)
    {
        return AuditChain.Verify(await ListForPatientAsync(patientId));
    }

    private sealed record AuditRow(
        long Id,
        Guid PatientId,
        DateTimeOffset At,
        string? ActorKind,
        string? ActorUri,
        string? ActorName,
        string? ActorOrg,
        string? ActorOrgUri,
        string Action,
        string? ClaimedPurpose,
        bool Granted,
        string? Reason,
        string? ChainHash
    )
    {
        public AuditEvent ToAuditEvent()
        {
            Party? actor = ActorUri is null
                ? null
                // Rows written before actor_org_uri existed fall back to the actor itself.
                : new Party(
                    Enum.Parse<PartyKind>(ActorKind!),
                    ActorUri,
                    ActorName!,
                    ActorOrg!,
                    ActorOrgUri ?? ActorUri
                );
            DenyReason? reason = Reason is null ? null : Enum.Parse<DenyReason>(Reason);
            return new AuditEvent(
                Id,
                PatientId,
                At,
                actor,
                Action,
                ClaimedPurpose,
                Granted,
                reason,
                ChainHash
            );
        }
    }
}
