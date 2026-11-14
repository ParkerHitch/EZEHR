namespace EZEHR.Models;

/// <summary>
/// Which of an organization's audit events to return. Every filter is optional and they combine: Since is inclusive,
/// Until exclusive. ProviderPatientId is the caller's own id for the patient (provider_links). Pages run by event id:
/// pass the previous page's NextAfterId as AfterId.
/// </summary>
public sealed record OrganizationAuditFilter(
    string? ClinicianUri,
    Guid? PatientId,
    string? ProviderPatientId,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    long AfterId,
    int Limit
);

/// <summary>
/// One access attempt by someone the organization vouches for. ProviderPatientId is the organization's own id for
/// the patient, when the patient is linked to it.
/// </summary>
public sealed record OrganizationAuditEvent(
    long Id,
    DateTimeOffset At,
    Guid PatientId,
    string? ProviderPatientId,
    PartyKind ActorKind,
    string ActorUri,
    string ActorName,
    string Action,
    string? ClaimedPurpose,
    bool Granted,
    DenyReason? Reason
);

/// <summary>A clinician seen acting for the organization. DisplayName is the name on their most recent event.</summary>
public sealed record OrganizationClinician(
    string ClinicianUri,
    string DisplayName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int EventCount,
    int DeniedCount
);
