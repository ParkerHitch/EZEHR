using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Security.Pki;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests;

/// <summary>A throwaway local CA plus helpers to issue certificates with private keys, for tests.</summary>
public sealed class TestPki
{
    public static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Clock { get; } = new(Start);
    public LocalCa Ca { get; }
    public CertificateValidator Validator { get; }
    public OrgDirectory Directory { get; } = new();

    public TestPki()
    {
        Ca = new LocalCa(Clock);
        Validator = new CertificateValidator([Ca.Root], [], Clock);
    }

    public X509Certificate2 Issue(string subjectName, bool rsa = false, int validityDays = 365, string csrSubject = "CN=ignored")
    {
        if (rsa)
        {
            var key = RSA.Create(2048);
            var csr = new CertificateRequest(csrSubject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
            var issued = Ca.IssueAsync(csr, subjectName, validityDays, CancellationToken.None).GetAwaiter().GetResult();
            return issued.Leaf.CopyWithPrivateKey(key);
        }
        else
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = new CertificateRequest(csrSubject, key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
            var issued = Ca.IssueAsync(csr, subjectName, validityDays, CancellationToken.None).GetAwaiter().GetResult();
            return issued.Leaf.CopyWithPrivateKey(key);
        }
    }

    /// <summary>A self-signed certificate with the clientAuth EKU, like the data broker's.</summary>
    public static X509Certificate2 SelfSigned(string cn, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CertificateValidator.ClientAuthOid)], false));
        return req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(365));
    }
}
