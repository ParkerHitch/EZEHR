using System.Text.Json.Nodes;

namespace EZEHR.Models;

/// <summary>A share or grant change. Grant is set when it succeeded, Error when it didn't.</summary>
public sealed record GrantResult(bool Succeeded, Grant? Grant, GrantError? Error);

/// <summary>The provider record an invite points at. Redeeming the invite links it to a patient.</summary>
public sealed record ProviderLink(string ProviderUri, string ProviderPatientId);

public sealed record InviteResult(bool Succeeded, ProviderLink? Link, InviteError? Error);

public sealed record CreateEnrollmentInviteResult(
    bool Allowed,
    DenyReason? Reason,
    string? InviteCode,
    string? EnrollmentUrl
);

/// <summary>Allowed is the caller check; Error is set when an allowed caller sent records that were refused.</summary>
public sealed record PushSignedRecordsResult(
    bool Allowed,
    DenyReason? Reason,
    RecordError? Error,
    int Stored,
    int AlreadyStored,
    int Replaced
);

public sealed record ReadEgfrLabsResult(
    bool Allowed,
    DenyReason? Reason,
    IReadOnlyList<SignedEntry> EgfrLabs
);

/// <summary>
/// Grant is the grant the access check decided on (pending, ended or active); PatientName is set when allowed.
/// UnverifiedCount is how many stored entries failed verification and aren't in Entries.
/// </summary>
public sealed record ReadFullRecordResult(
    bool Allowed,
    DenyReason? Reason,
    IReadOnlyList<SignedEntry> Entries,
    Grant? Grant,
    string? PatientName,
    int UnverifiedCount = 0
);

public sealed record WriteSignedNoteResult(
    bool Allowed,
    DenyReason? Reason,
    RecordError? Error,
    bool Stored,
    Guid? EntryId
);

/// <summary>The patient's whole record as a FHIR R4 collection Bundle, and the file name to download it as.</summary>
public sealed record ExportPatientRecordResult(string FileName, JsonObject Bundle);

/// <summary>NextAfterId is set when another page follows; pass it back as AfterId.</summary>
public sealed record ReadOrganizationAuditResult(
    bool Allowed,
    DenyReason? Reason,
    IReadOnlyList<OrganizationAuditEvent> Events,
    long? NextAfterId
);

public sealed record ListOrganizationCliniciansResult(
    bool Allowed,
    DenyReason? Reason,
    IReadOnlyList<OrganizationClinician> Clinicians
);
