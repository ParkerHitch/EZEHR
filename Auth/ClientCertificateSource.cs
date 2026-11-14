using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Auth;

/// <summary>Where the TLS client certificate for this request comes from.</summary>
public interface IClientCertificateSource
{
    Task<X509Certificate2?> GetAsync(HttpContext ctx);
}

/// <summary>
/// Production source: the certificate the TLS layer verified possession of. Locally that is Kestrel's mutual TLS;
/// on App Service, certificate forwarding (App:ClientCertificateHeader = X-ARR-ClientCert) fills the same property.
/// </summary>
public sealed class ConnectionClientCertificateSource : IClientCertificateSource
{
    public async Task<X509Certificate2?> GetAsync(HttpContext ctx) =>
        ctx.Connection.ClientCertificate ?? await ctx.Connection.GetClientCertificateAsync(ctx.RequestAborted);
}
