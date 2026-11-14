using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Services;

/// <summary>
/// Production source: the certificate the TLS layer verified possession of. Locally that is Kestrel's mutual TLS;
/// on App Service, certificate forwarding (App:ClientCertificateHeader = X-ARR-ClientCert) fills the same property.
/// </summary>
public sealed class ConnectionClientCertificateService : IClientCertificateService
{
    public async Task<X509Certificate2?> GetAsync(HttpContext httpContext)
    {
        return httpContext.Connection.ClientCertificate
            ?? await httpContext.Connection.GetClientCertificateAsync(httpContext.RequestAborted);
    }
}
