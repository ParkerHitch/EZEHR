using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Domain;

namespace EZEHR.Security.Pki;

public sealed record CertCheck(bool Ok, DenyReason? Reason);

/// <summary>
/// Checks that a certificate chains to one of our trust anchors (the EZCA root), is inside its validity period,
/// and carries the clientAuth EKU. Allows 2 minutes of clock skew around notBefore.
/// </summary>
public sealed class CertificateValidator(IEnumerable<X509Certificate2> roots, IEnumerable<X509Certificate2> intermediates, TimeProvider time)
{
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    private readonly X509Certificate2[] roots = roots.ToArray();
    private readonly X509Certificate2[] intermediates = intermediates.ToArray();

    public CertCheck Validate(X509Certificate2 leaf, IEnumerable<X509Certificate2>? extra = null) => Validate(leaf, extra, time.GetUtcNow());

    public CertCheck Validate(X509Certificate2 leaf, IEnumerable<X509Certificate2>? extra, DateTimeOffset at)
    {
        if (roots.Length == 0) return new CertCheck(false, DenyReason.UntrustedCertificate);
        if (!HasClientAuth(leaf)) return new CertCheck(false, DenyReason.UntrustedCertificate);

        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.AddRange(roots);
        policy.ExtraStore.AddRange(intermediates);
        if (extra is not null)
            foreach (var c in extra) policy.ExtraStore.Add(c);
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.DisableCertificateDownloads = true;
        policy.ApplicationPolicy.Add(new Oid(ClientAuthOid));
        policy.VerificationTime = (at + Skew).UtcDateTime;
        policy.VerificationTimeIgnored = false;

        if (chain.Build(leaf)) return new CertCheck(true, null);

        var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (acc, s) => acc | s.Status);
        foreach (var element in chain.ChainElements)
            foreach (var s in element.ChainElementStatus)
                flags |= s.Status;

        const X509ChainStatusFlags timeFlags = X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested;
        if ((flags & ~timeFlags) != X509ChainStatusFlags.NoError) return new CertCheck(false, DenyReason.UntrustedCertificate);
        return new CertCheck(false, DenyReason.CertificateExpired);
    }

    private static bool HasClientAuth(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthOid));

    /// <summary>Loads every certificate in a PEM file (trust anchors or intermediates). A missing file yields none.</summary>
    public static IReadOnlyList<X509Certificate2> LoadPem(string path)
    {
        if (!File.Exists(path)) return [];
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(path);
        return collection.ToArray();
    }
}
