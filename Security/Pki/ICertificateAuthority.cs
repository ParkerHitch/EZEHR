using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Security.Pki;

/// <summary>
/// Issues organization and app certificates. EzcaCa is the real one; LocalCa is for tests and the fallback.
/// Every certificate carries the clientAuth EKU, and the CSR's own subject is ignored in favor of subjectName.
/// </summary>
public interface ICertificateAuthority
{
    Task<IssuedCertificate> IssueAsync(string csrPem, string subjectName, int validityDays, CancellationToken ct);
}

/// <summary>The issued leaf (public only) and its chain up to and including the root.</summary>
public sealed record IssuedCertificate(X509Certificate2 Leaf, IReadOnlyList<X509Certificate2> Chain);
