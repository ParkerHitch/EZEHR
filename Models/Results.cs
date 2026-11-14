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
    int AlreadyStored
);

public sealed record ReadEgfrLabsResult(
    bool Allowed,
    DenyReason? Reason,
    IReadOnlyList<SignedEntry> EgfrLabs
);

public sealed record ReadFullRecordResult(
    bool Allowed,
    DenyReason? Reason,
    IReadOnlyList<SignedEntry> Entries
);

public sealed record WriteSignedNoteResult(
    bool Allowed,
    DenyReason? Reason,
    RecordError? Error,
    bool Stored,
    Guid? EntryId
);
