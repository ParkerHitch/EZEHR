using EZEHR.Models;

namespace EZEHR.Services;

/// <summary>
/// Issues organization and app certificates. EZCA is the real one; LocalCertificateAuthorityService is for tests
/// and the fallback. Every certificate carries the clientAuth EKU, and the CSR's own subject is ignored in favor
/// of subjectName.
/// </summary>
public interface ICertificateAuthorityService
{
    Task<IssuedCertificate> IssueAsync(
        string csrPem,
        string subjectName,
        int validityDays,
        CancellationToken cancellationToken
    );
}
