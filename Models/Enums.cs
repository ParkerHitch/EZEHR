namespace EZEHR.Models;

public enum PartyKind
{
    Clinician,
    Provider,
    App,
    Platform,
}

/// <summary>Full: clinicians read everything and write notes. LabsAndNotes: apps read lab Observations and write notes.</summary>
public enum GrantScope
{
    Full,
    LabsAndNotes,
}

public enum GrantStatus
{
    Pending,
    Active,
    Denied,
    Revoked,
    Expired,
}

public enum ShareDuration
{
    FiveMinutes,
    OneHour,
    FourHours,
    EndOfDay,
}

/// <summary>Why a caller was refused. NetworkController maps each one to 401 or 403.</summary>
public enum DenyReason
{
    NoCertificate,
    UntrustedCertificate,
    CertificateExpired,
    NotSignedIn,
    ShiftEnded,
    NoGrant,
    GrantPending,
    GrantExpired,
    GrantRevoked,
    ScopeDenied,

    /// <summary>A verified caller of the wrong kind, such as an app creating an invite.</summary>
    WrongPartyKind,
}

public enum AccessAction
{
    ReadRecord,
    ReadLabs,
    WriteNote,
}

/// <summary>Expected failures when sharing: a share code that can't be used, or a grant that can't be changed.</summary>
public enum GrantError
{
    OfferUnknown,
    OfferUsed,
    OfferExpired,
    GrantUnknown,
    GrantNotPending,
}

public enum InviteError
{
    InviteUnknown,
    InviteUsed,
    InviteExpired,
    InviteLinkedElsewhere,
}

/// <summary>Why an allowed caller's records or notes were refused.</summary>
public enum RecordError
{
    PatientNotLinked,
    BadSignature,
    SignerMismatch,
    ResourceIdRequired,
    NotACommunication,
}
