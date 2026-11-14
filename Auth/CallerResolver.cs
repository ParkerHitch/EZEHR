using System.Security.Cryptography.X509Certificates;
using EZEHR.Domain;
using EZEHR.Security.Pki;

namespace EZEHR.Auth;

/// <summary>The network door: turns a client certificate into a Caller. No certificate, a bad chain, or an unknown name is unverified.</summary>
public sealed class CallerResolver(CertificateValidator validator, OrgDirectory directory, TimeProvider time)
{
    public Caller Resolve(X509Certificate2? cert)
    {
        if (cert is null) return new UnverifiedCaller(DenyReason.NoCertificate);
        var check = validator.Validate(cert, null, time.GetUtcNow());
        if (!check.Ok) return new UnverifiedCaller(check.Reason ?? DenyReason.UntrustedCertificate);
        return directory.Resolve(cert) is { } party
            ? new VerifiedCaller(party, null)
            : new UnverifiedCaller(DenyReason.UntrustedCertificate);
    }
}
