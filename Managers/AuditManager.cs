using System.Data.Common;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>
/// Append-only log of every access attempt for a patient, granted or denied. The patient sees all of it. An attempt
/// on an id that isn't a patient is not logged, so callers can't fill the table by guessing ids.
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
        long? id = await connection.ExecuteScalarAsync<long?>(
            """
            INSERT INTO audit_events(patient_id, at, actor_kind, actor_uri, actor_name, actor_org,
                                     actor_org_uri, action, claimed_purpose, granted, reason)
            OUTPUT inserted.id
            SELECT @patientId, @at, @kind, @uri, @name, @org, @orgUri, @action, @purpose, @granted,
                   @reason
            WHERE EXISTS (SELECT 1 FROM patients WHERE id = @patientId)
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
            },
            transaction
        );
        if (id is not long storedId)
        {
            return null;
        }
        return new AuditEvent(storedId, patientId, at, actor, action, purpose, granted, reason);
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
                   reason AS Reason
            FROM audit_events
            WHERE patient_id = @patientId AND id > @afterId
            ORDER BY id
            """,
            new { patientId, afterId }
        );
        return rows.Select(row => row.ToAuditEvent()).ToList();
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
        string? Reason
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
                reason
            );
        }
    }
}
