using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>Turns each front door's raw identity into a Caller. No certificate, a bad chain, or an unknown name is unverified.</summary>
public sealed class CallerManager(TimeProvider time, IOptions<AppOptions> options)
{
    /// <summary>The patient cookie scheme and authorization policy.</summary>
    public const string PatientScheme = "Patient";

    public const string PatientIdClaim = "patient_id";

    private readonly TrustAnchors _trustAnchors = CertificateValidator.LoadTrustAnchors(
        options.Value
    );

    public Caller FromCertificate(X509Certificate2? certificate)
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
        Party? party = OrgDirectory.FindByCertificate(certificate);
        if (party is null)
        {
            return new UnverifiedCaller(DenyReason.UntrustedCertificate);
        }
        return new VerifiedCaller(party, null);
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
}
