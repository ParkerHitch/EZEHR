using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests;

/// <summary>A throwaway local CA plus helpers to issue certificates with private keys, for tests.</summary>
public sealed class TestPki
{
    public static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    public TestPki()
    {
        Ca = new LocalCertificateAuthorityService(
            LocalCertificateAuthorityService.CreateRoot(Clock),
            Clock
        );
        TrustAnchors = new TrustAnchors([Ca.Root], []);
    }

    public FakeTimeProvider Clock { get; } = new(Start);

    public LocalCertificateAuthorityService Ca { get; }

    public TrustAnchors TrustAnchors { get; }

    public X509Certificate2 Issue(
        string subjectName,
        bool rsa = false,
        int validityDays = 365,
        string csrSubject = "CN=ignored"
    )
    {
        if (rsa)
        {
            RSA rsaKey = RSA.Create(2048);
            string rsaCsr = new CertificateRequest(
                csrSubject,
                rsaKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            ).CreateSigningRequestPem();
            return IssueFor(rsaCsr, subjectName, validityDays).CopyWithPrivateKey(rsaKey);
        }

        ECDsa ecdsaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string ecdsaCsr = new CertificateRequest(
            csrSubject,
            ecdsaKey,
            HashAlgorithmName.SHA256
        ).CreateSigningRequestPem();
        return IssueFor(ecdsaCsr, subjectName, validityDays).CopyWithPrivateKey(ecdsaKey);
    }

    /// <summary>A self-signed certificate with the clientAuth EKU, like the data broker's.</summary>
    public static X509Certificate2 SelfSigned(string commonName, DateTimeOffset now)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid(CertificateValidator.ClientAuthenticationOid)],
                false
            )
        );
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(365));
    }

    private X509Certificate2 IssueFor(string csrPem, string subjectName, int validityDays)
    {
        IssuedCertificate issued = Ca.IssueAsync(
                csrPem,
                subjectName,
                validityDays,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        return issued.Leaf;
    }
}
