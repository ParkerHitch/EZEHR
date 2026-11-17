namespace EZEHR.Models;

/// <summary>
/// One access attempt, granted or denied. A null Actor means the caller was not verified. ChainHash is the event's
/// fingerprint in the patient's hash chain (AuditChain); null only on events from before fingerprints were kept.
/// </summary>
public sealed record AuditEvent(
    long Id,
    Guid PatientId,
    DateTimeOffset At,
    Party? Actor,
    string Action,
    string? ClaimedPurpose,
    bool Granted,
    DenyReason? Reason,
    string? ChainHash = null
);
