namespace EZEHR.Domain;

public enum PartyKind { Clinician, Provider, App, Platform }

/// <summary>Full: clinicians read everything and write notes. LabsAndNotes: apps read lab Observations and write notes.</summary>
public enum GrantScope { Full, LabsAndNotes }

public enum GrantStatus { Pending, Active, Denied, Revoked, Expired }

public enum ShareDuration { FiveMinutes, OneHour, FourHours, EndOfDay }

public enum DenyReason
{
    NoCertificate, UntrustedCertificate, CertificateExpired, NotSignedIn, ShiftEnded,
    NoGrant, GrantPending, GrantExpired, GrantRevoked, ScopeDenied
}

public enum AccessAction { ReadRecord, ReadLabs, WriteNote }
