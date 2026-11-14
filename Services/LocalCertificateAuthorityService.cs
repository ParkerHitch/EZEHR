using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;

namespace EZEHR.Services;

/// <summary>
/// An in-process ECDSA P-256 certificate authority. Tests and the "Local" fallback only; EZCA issues the real
/// certificates. Takes a root with its private key, from CreateRoot or from .pki/local-root.pfx.
/// </summary>
public sealed class LocalCertificateAuthorityService(X509Certificate2 root, TimeProvider time)
    : ICertificateAuthorityService
{
    /// <summary>The root certificate, with its private key.</summary>
    public X509Certificate2 Root { get; } = RequirePrivateKey(root);

    /// <summary>Creates a fresh root valid for 10 years.</summary>
    public static X509Certificate2 CreateRoot(
        TimeProvider time,
        string rootName = "EZEHR Local Test Root"
    )
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={rootName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true
            )
        );
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false)
        );
        DateTimeOffset now = time.GetUtcNow();
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
    }

    public Task<IssuedCertificate> IssueAsync(
        string csrPem,
        string subjectName,
        int validityDays,
        CancellationToken cancellationToken
    )
    {
        // Verifies the CSR's self-signature; throws CryptographicException if it was tampered with.
        CertificateRequest csr = CertificateRequest.LoadSigningRequestPem(
            csrPem,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default,
            RSASignaturePadding.Pkcs1
        );

        bool isRsa = csr.PublicKey.GetRSAPublicKey() is not null;
        CertificateRequest request = new(
            new X500DistinguishedName($"CN={subjectName}"),
            csr.PublicKey,
            HashAlgorithmName.SHA256,
            isRsa ? RSASignaturePadding.Pkcs1 : null
        );
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature
                    | (isRsa ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.KeyAgreement),
                true
            )
        );
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid(CertificateValidator.ClientAuthenticationOid)],
                false
            )
        );
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false)
        );
        request.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false)
        );
        SubjectAlternativeNameBuilder subjectAlternativeNames = new();
        subjectAlternativeNames.AddDnsName(subjectName);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        DateTimeOffset now = time.GetUtcNow();
        DateTimeOffset notAfter = now.AddDays(validityDays);
        DateTimeOffset rootNotAfter = new(Root.NotAfter.ToUniversalTime());
        if (notAfter > rootNotAfter)
        {
            notAfter = rootNotAfter;
        }

        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using ECDsa rootKey = Root.GetECDsaPrivateKey()!;
        X509Certificate2 leaf = request.Create(
            Root.SubjectName,
            X509SignatureGenerator.CreateForECDsa(rootKey),
            now.AddMinutes(-5),
            notAfter,
            serial
        );
        X509Certificate2 rootPublic = X509CertificateLoader.LoadCertificate(Root.RawData);
        return Task.FromResult(new IssuedCertificate(leaf, [rootPublic]));
    }

    private static X509Certificate2 RequirePrivateKey(X509Certificate2 root)
    {
        if (root.GetECDsaPublicKey() is null || !root.HasPrivateKey)
        {
            throw new ArgumentException(
                "The root certificate needs its ECDSA private key.",
                nameof(root)
            );
        }
        return root;
    }
}
