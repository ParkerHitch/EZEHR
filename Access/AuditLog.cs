using Dapper;
using EZEHR.Data;
using EZEHR.Domain;
using Microsoft.Data.Sqlite;

namespace EZEHR.Access;

/// <summary>Append-only log of every access attempt for a patient, granted or denied. The patient sees all of it.</summary>
public sealed class AuditLog(SqliteConnection db, TimeProvider time)
{
    public AuditEvent Append(Guid patientId, Party? actor, string action, string? claimedPurpose, bool granted, DenyReason? reason)
    {
        var at = time.GetUtcNow();
        var id = db.ExecuteScalar<long>("""
            INSERT INTO audit_events(patient_id, at, actor_kind, actor_uri, actor_name, actor_org, action, claimed_purpose, granted, reason)
            VALUES (@patient, @at, @kind, @uri, @name, @org, @action, @claimedPurpose, @granted, @reason);
            SELECT last_insert_rowid();
            """,
            new
            {
                patient = patientId.ToString(), at = Db.Time(at),
                kind = actor?.Kind.ToString(), uri = actor?.SubjectUri, name = actor?.DisplayName, org = actor?.OrgName,
                action, claimedPurpose, granted = granted ? 1 : 0, reason = reason?.ToString(),
            });
        return new AuditEvent(id, patientId, at, actor, action, claimedPurpose, granted, reason);
    }

    /// <summary>Events for one patient, oldest first, after the given id (for htmx polling).</summary>
    public IReadOnlyList<AuditEvent> List(Guid patientId, long afterId = 0) =>
        db.Query<Row>("""
            SELECT id, at, actor_kind AS ActorKind, actor_uri AS ActorUri, actor_name AS ActorName, actor_org AS ActorOrg,
                   action, claimed_purpose AS ClaimedPurpose, granted, reason
            FROM audit_events WHERE patient_id = @patient AND id > @afterId ORDER BY id
            """, new { patient = patientId.ToString(), afterId })
          .Select(r => new AuditEvent(r.Id, patientId, Db.Time(r.At),
              r.ActorUri is null ? null : new Party(Enum.Parse<PartyKind>(r.ActorKind!), r.ActorUri, r.ActorName!, r.ActorOrg!),
              r.Action, r.ClaimedPurpose, r.Granted != 0, r.Reason is null ? null : Enum.Parse<DenyReason>(r.Reason)))
          .ToList();

    private sealed class Row
    {
        public long Id { get; set; }
        public string At { get; set; } = "";
        public string? ActorKind { get; set; }
        public string? ActorUri { get; set; }
        public string? ActorName { get; set; }
        public string? ActorOrg { get; set; }
        public string Action { get; set; } = "";
        public string? ClaimedPurpose { get; set; }
        public long Granted { get; set; }
        public string? Reason { get; set; }
    }
}
