using System.Data.Common;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>Append-only log of every access attempt for a patient, granted or denied. The patient sees all of it.</summary>
public sealed class AuditManager(Db db, TimeProvider time)
{
    /// <summary>Longest claimed purpose kept. The broker chooses it, so it's cut to fit the column.</summary>
    public const int MaxClaimedPurposeLength = 200;

    public async Task<AuditEvent> AppendAsync(
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
    public async Task<AuditEvent> AppendAsync(
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
        long id = await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO audit_events(patient_id, at, actor_kind, actor_uri, actor_name, actor_org,
                                     action, claimed_purpose, granted, reason)
            OUTPUT inserted.id
            VALUES (@patientId, @at, @kind, @uri, @name, @org, @action, @purpose, @granted, @reason)
            """,
            new
            {
                patientId,
                at,
                kind = actor?.Kind.ToString(),
                uri = actor?.SubjectUri,
                name = actor?.DisplayName,
                org = actor?.OrgName,
                action,
                purpose,
                granted,
                reason = reason?.ToString(),
            },
            transaction
        );
        return new AuditEvent(id, patientId, at, actor, action, purpose, granted, reason);
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
                   actor_uri AS ActorUri, actor_name AS ActorName, actor_org AS ActorOrg,
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
                : new Party(Enum.Parse<PartyKind>(ActorKind!), ActorUri, ActorName!, ActorOrg!);
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
