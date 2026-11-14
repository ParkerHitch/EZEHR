namespace EZEHR.Domain;

/// <summary>One access attempt, granted or denied. A null Actor means the caller was not verified.</summary>
public sealed record AuditEvent(
    long Id,
    Guid PatientId,
    DateTimeOffset At,
    Party? Actor,
    string Action,
    string? ClaimedPurpose,
    bool Granted,
    DenyReason? Reason);
