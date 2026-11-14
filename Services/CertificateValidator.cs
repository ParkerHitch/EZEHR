using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;

namespace EZEHR.Services;

/// <summary>
/// Checks that a certificate chains to one of our trust anchors (the EZCA root), is inside its validity period,
/// and carries the clientAuth EKU. Allows 2 minutes of clock skew around notBefore.
/// </summary>
public static class CertificateValidator
{
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    public static CertCheck Validate(
        X509Certificate2 leaf,
        IEnumerable<X509Certificate2>? extra,
        DateTimeOffset at,
        TrustAnchors trustAnchors
    )
    {
        if (trustAnchors.Roots.Count == 0)
        {
            return new CertCheck(false, DenyReason.UntrustedCertificate);
        }
        if (!HasClientAuth(leaf))
        {
            return new CertCheck(false, DenyReason.UntrustedCertificate);
        }

        using X509Chain chain = new();
        X509ChainPolicy policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.AddRange(trustAnchors.Roots.ToArray());
        policy.ExtraStore.AddRange(trustAnchors.Intermediates.ToArray());
        if (extra is not null)
        {
            foreach (X509Certificate2 certificate in extra)
            {
                policy.ExtraStore.Add(certificate);
            }
        }
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.DisableCertificateDownloads = true;
        policy.ApplicationPolicy.Add(new Oid(ClientAuthOid));
        policy.VerificationTime = (at + Skew).UtcDateTime;
        policy.VerificationTimeIgnored = false;

        if (chain.Build(leaf))
        {
            return new CertCheck(true, null);
        }

        X509ChainStatusFlags flags = X509ChainStatusFlags.NoError;
        foreach (X509ChainStatus status in chain.ChainStatus)
        {
            flags |= status.Status;
        }
        foreach (X509ChainElement element in chain.ChainElements)
        {
            foreach (X509ChainStatus status in element.ChainElementStatus)
            {
                flags |= status.Status;
            }
        }

        const X509ChainStatusFlags timeFlags =
            X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested;
        if ((flags & ~timeFlags) != X509ChainStatusFlags.NoError)
        {
            return new CertCheck(false, DenyReason.UntrustedCertificate);
        }
        return new CertCheck(false, DenyReason.CertificateExpired);
    }

    /// <summary>Loads the trust anchors and intermediates named in AppOptions. A missing file yields none, so nothing is trusted.</summary>
    public static TrustAnchors LoadTrustAnchors(AppOptions options)
    {
        return new TrustAnchors(
            LoadPem(options.TrustAnchorsPemPath),
            LoadPem(options.IntermediatesPemPath)
        );
    }

    private static IReadOnlyList<X509Certificate2> LoadPem(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        X509Certificate2Collection collection = [];
        collection.ImportFromPemFile(path);
        return collection.ToArray();
    }

    private static bool HasClientAuth(X509Certificate2 certificate)
    {
        return certificate
            .Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension =>
                extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ClientAuthOid)
            );
    }
}
