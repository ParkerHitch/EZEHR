using EZEHR.Domain;

namespace EZEHR.Access;

/// <summary>
/// The one access check. Every read or write of a patient's record asks it first, and it writes exactly one
/// audit event per call, allowed or denied. Signing in proves who someone is; only the patient's grant lets them in.
/// </summary>
public sealed class AccessGate(GrantService grants, AuditLog audit, TimeProvider time)
{
    /// <summary>A clinician session lasts 10 hours from sign-in, never sliding.</summary>
    public static readonly TimeSpan ClinicianShift = TimeSpan.FromHours(10);

    public AccessDecision Authorize(Guid patientId, AccessAction action, Caller caller, string? claimedPurpose)
    {
        var decision = Decide(patientId, action, caller);
        audit.Append(patientId, (caller as VerifiedCaller)?.Party, action.ToString(), claimedPurpose, decision.Allowed, decision.Reason);
        return decision;
    }

    private AccessDecision Decide(Guid patientId, AccessAction action, Caller caller)
    {
        if (caller is UnverifiedCaller unverified) return Deny(unverified.Reason);
        var verified = (VerifiedCaller)caller;
        var now = time.GetUtcNow();

        if (verified.Party.Kind == PartyKind.Clinician)
        {
            if (verified.SessionStartedAt is not { } started) return Deny(DenyReason.NotSignedIn);
            if (now - started > ClinicianShift) return Deny(DenyReason.ShiftEnded);
        }

        var all = grants.ForGrantee(patientId, verified.Party.SubjectUri);
        if (all.Count == 0) return Deny(DenyReason.NoGrant);

        // Anything past its time is marked Expired before deciding.
        var current = all.Select(g =>
        {
            if (g.Status == GrantStatus.Active && g.ExpiresAt is { } end && now >= end)
            {
                grants.MarkExpired(g.Id);
                return g with { Status = GrantStatus.Expired };
            }
            return g;
        }).ToList();

        var active = current.FirstOrDefault(g => g.Status == GrantStatus.Active);
        if (active is null)
            return Deny(current[0].Status switch
            {
                GrantStatus.Pending => DenyReason.GrantPending,
                GrantStatus.Revoked or GrantStatus.Denied => DenyReason.GrantRevoked,
                GrantStatus.Expired => DenyReason.GrantExpired,
                _ => DenyReason.NoGrant,
            }, current[0]);

        if (active.Scope == GrantScope.LabsAndNotes && action == AccessAction.ReadRecord) return Deny(DenyReason.ScopeDenied, active);
        return new AccessDecision(true, active, null);
    }

    private static AccessDecision Deny(DenyReason reason, Grant? grant = null) => new(false, grant, reason);

    /// <summary>Certificate problems are 401; everything else a verified caller is refused is 403.</summary>
    public static bool IsAuthenticationFailure(DenyReason reason) =>
        reason is DenyReason.NoCertificate or DenyReason.UntrustedCertificate or DenyReason.CertificateExpired or DenyReason.NotSignedIn;
}
