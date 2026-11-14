namespace EZEHR.Models;

/// <summary>
/// Anyone who can act on a record. SubjectUri values:
/// clinician - urn:entra:{tid}:{oid} - Entra tenant ID and the user's object ID
/// provider - urn:provider:{slug} - slug of the CN of its EZCA client certificate
/// Third-Party App - urn:app:{slug} - slug of the CN of its EZCA client certificate
/// OrgUri is the organization that vouches for the party: a provider or app vouches for itself, and a clinician's
/// is the provider whose Entra tenant signed them in. The audit log records it, so an organization can audit its own.
/// </summary>
public sealed record Party(
    PartyKind Kind,
    string SubjectUri,
    string DisplayName,
    string OrgName,
    string OrgUri
);

/// <summary>What each front door (passkey, Entra, client certificate) turns its credential into.</summary>
public abstract record Caller;

public sealed record VerifiedCaller(Party Party, DateTimeOffset? SessionStartedAt) : Caller;

public sealed record UnverifiedCaller(DenyReason Reason) : Caller;
