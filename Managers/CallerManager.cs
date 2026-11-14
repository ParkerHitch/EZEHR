using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>Turns each front door's raw identity into a Caller. No certificate, a bad chain, or an unknown name is unverified.</summary>
public sealed class CallerManager(TimeProvider time, IOptions<AppOptions> options)
{
    private readonly TrustAnchors _trustAnchors = CertificateValidator.LoadTrustAnchors(
        options.Value
    );

    public Caller FromCertificate(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            return new UnverifiedCaller(DenyReason.NoCertificate);
        }
        CertCheck check = CertificateValidator.Validate(
            certificate,
            null,
            time.GetUtcNow(),
            _trustAnchors
        );
        if (!check.Ok)
        {
            return new UnverifiedCaller(check.Reason ?? DenyReason.UntrustedCertificate);
        }
        Party? party = OrgDirectory.Resolve(certificate);
        if (party is null)
        {
            return new UnverifiedCaller(DenyReason.UntrustedCertificate);
        }
        return new VerifiedCaller(party, null);
    }
}
