namespace EZEHR.Models;

/// <summary>
/// A patient's permission for one party. A null ExpiresAt means ongoing; only app grants are ongoing.
/// EmergencyReason is set only on emergency access: a clinician opened the record with the patient's emergency
/// code, without an approval, and this is the reason they gave.
/// </summary>
public sealed record Grant(
    Guid Id,
    Guid PatientId,
    Party Grantee,
    GrantScope Scope,
    string Purpose,
    GrantStatus Status,
    int? DurationMinutes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? EmergencyReason = null
);

public sealed record AccessDecision(bool Allowed, Grant? Grant, DenyReason? Reason);
