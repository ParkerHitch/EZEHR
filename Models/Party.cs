namespace EZEHR.Models;

/// <summary>
/// Anyone who can act on a record. SubjectUri values:
/// urn:entra:{tid}:{oid} (clinician), urn:provider:{slug}, urn:app:kidney-check, urn:platform:ezehr.
/// </summary>
public sealed record Party(PartyKind Kind, string SubjectUri, string DisplayName, string OrgName);

/// <summary>What each front door (passkey, Entra, client certificate) turns its credential into.</summary>
public abstract record Caller;

public sealed record VerifiedCaller(Party Party, DateTimeOffset? SessionStartedAt) : Caller;

public sealed record UnverifiedCaller(DenyReason Reason) : Caller;
