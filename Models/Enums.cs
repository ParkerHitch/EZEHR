namespace EZEHR.Models;

public enum PartyKind
{
    Clinician,
    Provider,
    App,
}

/// <summary>
/// Full: clinicians read everything, and write nothing (their organization's system writes, signed by it).
/// LabsAndNotes: apps read lab Observations and write notes.
/// MedicationsLabsAndNotes: the same, plus the medication list.
/// An app declares its scope in the organizations table; a grant keeps the scope the patient agreed to.
/// </summary>
public enum GrantScope
{
    Full,
    LabsAndNotes,
    MedicationsLabsAndNotes,
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

/// <summary>What a caller asked to do with a patient's record. Named after the network endpoints, and logged as-is.</summary>
public enum AccessAction
{
    ReadFullRecord,
    ReadEgfrLabs,
    ReadMedications,
    WriteSignedNote,
}

/// <summary>Expected failures when sharing: a share code that can't be used, or a grant that can't be changed.</summary>
public enum GrantError
{
    OfferUnknown,
    OfferUsed,
    OfferExpired,
    GrantUnknown,
    GrantNotPending,

    /// <summary>No patient has this emergency code. A patient who turned emergency access off has none.</summary>
    EmergencyCodeUnknown,

    /// <summary>Emergency access needs a stated reason; the patient and the clinician's organization both see it.</summary>
    EmergencyReasonMissing,
}

/// <summary>Why the platform couldn't register an organization.</summary>
public enum OrganizationError
{
    /// <summary>Its URI or certificate CN already has a row.</summary>
    OrganizationExists,

    /// <summary>An app asked to declare the Full scope, which is for clinicians only.</summary>
    AppScopeNotAllowed,
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
    MissingOrInvalidResourceId,
    NotACommunication,

    /// <summary>The JWS iat is in the future, or too old for a record that is signed when it is sent.</summary>
    SignedAtOutOfRange,
}

/// <summary>What storing a signed record did. A record is one (patient, signer, resource type, resource id).</summary>
public enum StoreOutcome
{
    Stored,
    AlreadyStored,

    /// <summary>The signer sent a changed version, signed at or after the stored one, and it replaced it.</summary>
    Replaced,

    /// <summary>The signer sent a changed version signed before the stored one, which is kept.</summary>
    OlderThanStored,
}
