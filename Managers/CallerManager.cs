using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace EZEHR.Managers;

/// <summary>Turns each front door's raw identity into a Caller. No certificate, a bad chain, or an unknown name is unverified.</summary>
public sealed class CallerManager(
    TimeProvider time,
    IOptions<AppOptions> options,
    OrganizationManager organizationManager
)
{
    /// <summary>The patient cookie scheme and authorization policy.</summary>
    public const string PatientScheme = "Patient";

    public const string PatientIdClaim = "patient_id";

    /// <summary>The clinician cookie scheme and authorization policy.</summary>
    public const string ClinicianScheme = "Clinician";

    /// <summary>The OpenID Connect scheme that signs clinicians in with Entra ID and hands off to the clinician cookie.</summary>
    public const string ClinicianOidcScheme = "ClinicianOidc";

    /// <summary>The Entra app role an organization assigns to the accounts it vouches for as clinicians.</summary>
    public const string ClinicianRole = "Clinician";

    /// <summary>When the clinician signed in, in Unix seconds. The 10-hour shift counts from here.</summary>
    public const string SessionStartedAtClaim = "session_started_at";

    private readonly TrustAnchors _trustAnchors = CertificateValidator.LoadTrustAnchors(
        options.Value
    );

    public async Task<Caller> FromCertificateAsync(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return new UnverifiedCaller(DenyReason.NoCertificate);
        }
        CertificateCheck check = CertificateValidator.Validate(
            certificate,
            null,
            time.GetUtcNow(),
            _trustAnchors
        );
        if (!check.Valid)
        {
            return new UnverifiedCaller(check.Reason ?? DenyReason.UntrustedCertificate);
        }
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        Party? party = directory.FindByCertificate(certificate);
        if (party is null)
        {
            return new UnverifiedCaller(DenyReason.UntrustedCertificate);
        }
        return new VerifiedCaller(party, null);
    }

    /// <summary>
    /// The platform operator: an EZCA certificate that is valid now and whose SHA-256 thumbprint is the one pinned in
    /// App:PlatformCertificateThumbprint. Any other certificate, or none pinned, is unverified.
    /// </summary>
    public Caller FromPlatformCertificate(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return new UnverifiedCaller(DenyReason.NoCertificate);
        }
        CertificateCheck check = CertificateValidator.Validate(
            certificate,
            null,
            time.GetUtcNow(),
            _trustAnchors
        );
        if (!check.Valid)
        {
            return new UnverifiedCaller(check.Reason ?? DenyReason.UntrustedCertificate);
        }
        string? pinned = options.Value.PlatformCertificateThumbprint;
        string thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (
            string.IsNullOrWhiteSpace(pinned)
            || !string.Equals(pinned.Trim(), thumbprint, StringComparison.OrdinalIgnoreCase)
        )
        {
            return new UnverifiedCaller(DenyReason.UntrustedCertificate);
        }
        return new PlatformCaller();
    }

    /// <summary>The principal the patient cookie carries after a passkey ceremony.</summary>
    public ClaimsPrincipal PatientPrincipal(Guid patientId)
    {
        ClaimsIdentity identity = new(
            [new Claim(PatientIdClaim, patientId.ToString())],
            PatientScheme
        );
        return new ClaimsPrincipal(identity);
    }

    /// <summary>The signed-in patient's id, or null when the principal isn't a patient.</summary>
    public Guid? PatientIdFrom(ClaimsPrincipal? user)
    {
        string? value = user?.FindFirstValue(PatientIdClaim);
        if (Guid.TryParse(value, out Guid patientId))
        {
            return patientId;
        }
        return null;
    }

    /// <summary>Stamped on the clinician's principal at sign-in, whichever way they signed in.</summary>
    public Claim SessionStartedClaim()
    {
        return new Claim(SessionStartedAtClaim, time.GetUtcNow().ToUnixTimeSeconds().ToString());
    }

    /// <summary>
    /// Signing in proves who someone is; this decides whether they count as a clinician here: their tenant must
    /// belong to a provider in the organizations table, and that provider must have given them the Clinician role.
    /// </summary>
    public async Task<ClinicianCheck> EvaluateClinicianAsync(ClaimsPrincipal user)
    {
        string? tenantId = user.GetTenantId();
        if (!Guid.TryParse(tenantId, out Guid tenantGuid))
        {
            return new ClinicianCheck(false, null, null, Copy.UntrustedOrg);
        }
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        Party? provider = directory.FindProviderByTenant(tenantGuid);
        if (provider is null)
        {
            return new ClinicianCheck(false, null, null, Copy.UntrustedOrg);
        }
        string? objectId = user.GetObjectId();
        bool hasRole =
            user.HasClaim("roles", ClinicianRole) || user.HasClaim(ClaimTypes.Role, ClinicianRole);
        if (objectId is null || !hasRole)
        {
            return new ClinicianCheck(false, null, null, Copy.NotAClinician);
        }

        string displayName = user.FindFirstValue("name")
            ?? user.FindFirstValue(ClaimTypes.Name)
            ?? objectId;
        Party party = new(
            PartyKind.Clinician,
            OrgDirectory.ClinicianUri(tenantId, objectId),
            displayName,
            provider.DisplayName,
            provider.SubjectUri
        );
        return new ClinicianCheck(true, party, SessionStartedAt(user), null);
    }

    /// <summary>The caller for the access check. A session without a start time is refused there as NotSignedIn.</summary>
    public async Task<Caller> FromClinicianAsync(ClaimsPrincipal user)
    {
        ClinicianCheck check = await EvaluateClinicianAsync(user);
        if (!check.Ok)
        {
            return new UnverifiedCaller(DenyReason.NotSignedIn);
        }
        return new VerifiedCaller(check.Party!, check.SessionStartedAt);
    }

    private static DateTimeOffset? SessionStartedAt(ClaimsPrincipal user)
    {
        string? value = user.FindFirstValue(SessionStartedAtClaim);
        if (long.TryParse(value, out long unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        return null;
    }
}
