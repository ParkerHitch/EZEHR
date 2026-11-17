using EZEHR.Models;

namespace EZEHR.Managers;

/// <summary>
/// The one access check. Every read or write of a patient's record asks it first, and it writes exactly one
/// audit event per call, allowed or denied. Signing in proves who someone is; only the patient's grant lets them in.
/// </summary>
public sealed class AccessManager(
    GrantManager grantManager,
    AuditManager auditManager,
    TimeProvider time
)
{
    /// <summary>A clinician session lasts 10 hours from sign-in, never sliding.</summary>
    public static readonly TimeSpan ClinicianShift = TimeSpan.FromHours(10);

    public async Task<AccessDecision> AuthorizeAsync(
        Guid patientId,
        AccessAction action,
        Caller caller,
        string? claimedPurpose
    )
    {
        AccessDecision decision = await DecideAccessAsync(patientId, action, caller);
        Party? actor = caller is VerifiedCaller verified ? verified.Party : null;
        // A read made under emergency access says so in the log, so the patient and the clinician's organization
        // can tell it from a read the patient approved.
        bool underEmergencyAccess = decision is { Allowed: true, Grant.EmergencyReason: not null };
        await auditManager.AppendAsync(
            patientId,
            actor,
            action.ToString(),
            claimedPurpose ?? (underEmergencyAccess ? GrantManager.EmergencyPurpose : null),
            decision.Allowed,
            decision.Reason
        );
        return decision;
    }

    /// <summary>
    /// The same decision as AuthorizeAsync, without the audit event. Only for status polls that return no record
    /// data, like the clinician's open record checking every few seconds whether it should lock itself.
    /// </summary>
    public async Task<AccessDecision> CheckAccessWithoutAuditAsync(
        Guid patientId,
        AccessAction action,
        Caller caller
    )
    {
        return await DecideAccessAsync(patientId, action, caller);
    }

    private async Task<AccessDecision> DecideAccessAsync(
        Guid patientId,
        AccessAction action,
        Caller caller
    )
    {
        if (caller is UnverifiedCaller unverified)
        {
            return Deny(unverified.Reason);
        }
        VerifiedCaller verified = (VerifiedCaller)caller;
        DateTimeOffset now = time.GetUtcNow();

        if (verified.Party.Kind == PartyKind.Clinician)
        {
            if (verified.SessionStartedAt is not DateTimeOffset started)
            {
                return Deny(DenyReason.NotSignedIn);
            }
            if (now - started > ClinicianShift)
            {
                return Deny(DenyReason.ShiftEnded);
            }
        }

        IReadOnlyList<Grant> grants = await grantManager.ListForGranteeAsync(
            patientId,
            verified.Party.SubjectUri
        );
        if (grants.Count == 0)
        {
            return Deny(DenyReason.NoGrant);
        }

        // Anything past its time is marked Expired before deciding.
        List<Grant> current = [];
        foreach (Grant grant in grants)
        {
            if (
                grant.Status == GrantStatus.Active
                && grant.ExpiresAt is DateTimeOffset end
                && now >= end
            )
            {
                await grantManager.MarkExpiredAsync(grant.Id);
                current.Add(grant with { Status = GrantStatus.Expired });
            }
            else
            {
                current.Add(grant);
            }
        }

        Grant? active = current.FirstOrDefault(grant => grant.Status == GrantStatus.Active);
        if (active is null)
        {
            Grant newest = current[0];
            DenyReason reason = newest.Status switch
            {
                GrantStatus.Pending => DenyReason.GrantPending,
                GrantStatus.Revoked or GrantStatus.Denied => DenyReason.GrantRevoked,
                GrantStatus.Expired => DenyReason.GrantExpired,
                _ => DenyReason.NoGrant,
            };
            return Deny(reason, newest);
        }

        if (!ScopeAllows(active.Scope, action))
        {
            return Deny(DenyReason.ScopeDenied, active);
        }
        return new AccessDecision(true, active, null);
    }

    /// <summary>People read; writes come only from certificate-holding apps (and providers, through ingest).</summary>
    private static bool ScopeAllows(GrantScope scope, AccessAction action)
    {
        return scope switch
        {
            GrantScope.Full => action is AccessAction.ReadFullRecord or AccessAction.ReadEgfrLabs,
            GrantScope.LabsAndNotes => action
                is AccessAction.ReadEgfrLabs
                    or AccessAction.WriteSignedNote,
            _ => false,
        };
    }

    private static AccessDecision Deny(DenyReason reason, Grant? grant = null)
    {
        return new AccessDecision(false, grant, reason);
    }
}
