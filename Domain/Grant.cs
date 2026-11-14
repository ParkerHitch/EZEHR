namespace EZEHR.Domain;

/// <summary>A patient's permission for one party. A null ExpiresAt means ongoing; only app grants are ongoing.</summary>
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
    DateTimeOffset? RevokedAt);

public sealed record AccessDecision(bool Allowed, Grant? Grant, DenyReason? Reason);
