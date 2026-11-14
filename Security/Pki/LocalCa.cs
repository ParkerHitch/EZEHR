using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Security.Pki;

/// <summary>An in-process ECDSA P-256 certificate authority. Tests and the "Local" fallback only; EZCA issues the real certificates.</summary>
public sealed class LocalCa : ICertificateAuthority
{
    private readonly TimeProvider time;

    /// <summary>The root certificate, with its private key.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>Creates a fresh root valid for 10 years.</summary>
    public LocalCa(TimeProvider time, string rootName = "EZEHR Local Test Root")
    {
        this.time = time;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={rootName}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var now = time.GetUtcNow();
        Root = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
    }

    /// <summary>Loads an existing root (with private key), e.g. .pki/local-root.pfx.</summary>
    public LocalCa(X509Certificate2 rootWithKey, TimeProvider time)
    {
        if (rootWithKey.GetECDsaPublicKey() is null || !rootWithKey.HasPrivateKey)
            throw new ArgumentException("The root certificate needs its ECDSA private key.", nameof(rootWithKey));
        this.time = time;
        Root = rootWithKey;
    }

    public Task<IssuedCertificate> IssueAsync(string csrPem, string subjectName, int validityDays, CancellationToken ct)
    {
        // Verifies the CSR's self-signature; throws CryptographicException if it was tampered with.
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);

        var req = new CertificateRequest(new X500DistinguishedName($"CN={subjectName}"), csr.PublicKey, HashAlgorithmName.SHA256,
            csr.PublicKey.GetRSAPublicKey() is null ? null : RSASignaturePadding.Pkcs1);
        var isRsa = csr.PublicKey.GetRSAPublicKey() is not null;
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | (isRsa ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.KeyAgreement), true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CertificateValidator.ClientAuthOid)], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(subjectName);
        req.CertificateExtensions.Add(san.Build());

        var now = time.GetUtcNow();
        var notAfter = now.AddDays(validityDays);
        var rootNotAfter = new DateTimeOffset(Root.NotAfter.ToUniversalTime());
        if (notAfter > rootNotAfter) notAfter = rootNotAfter;

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var rootKey = Root.GetECDsaPrivateKey()!;
        var leaf = req.Create(Root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), now.AddMinutes(-5), notAfter, serial);
        var rootPublic = X509CertificateLoader.LoadCertificate(Root.RawData);
        return Task.FromResult(new IssuedCertificate(leaf, [rootPublic]));
    }
}
