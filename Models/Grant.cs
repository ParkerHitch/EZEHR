namespace EZEHR.Models;

/// <summary>
/// A patient's permission for one party. A null ExpiresAt means ongoing; only app grants are ongoing.
/// EmergencyReason is set only on emergency access: a clinician opened the record with the patient's emergency
/// code, without an approval, and this is the reason they gave. EncounterId is the visit, in the clinician's
/// organization's own numbering, that the organization launched the clinician from; app grants have none.
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
    string? EmergencyReason = null,
    string? EncounterId = null
);

public sealed record AccessDecision(bool Allowed, Grant? Grant, DenyReason? Reason);
