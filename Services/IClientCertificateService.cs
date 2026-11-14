using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Services;

/// <summary>Where the TLS client certificate for this request comes from.</summary>
public interface IClientCertificateService
{
    Task<X509Certificate2?> GetAsync(HttpContext httpContext);
}
