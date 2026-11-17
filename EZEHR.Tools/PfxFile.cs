using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Tools;

/// <summary>
/// A certificate with its private key and its chain, as a password-less PKCS#12 file in .pki/. The files never
/// leave the laptop, and .pki/ is git-ignored.
/// </summary>
public sealed record PfxFile(X509Certificate2 Certificate, IReadOnlyList<X509Certificate2> Chain)
{
    public static PfxFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ToolException(
                $"{path} not found. Run `ezehr-tools certs issue` (or `certs broker`) first."
            );
        }
        X509Certificate2Collection collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(
            path,
            string.Empty,
            X509KeyStorageFlags.Exportable
        );
        X509Certificate2 certificate = collection.Single(candidate => candidate.HasPrivateKey);
        List<X509Certificate2> chain = collection
            .Where(candidate => !candidate.HasPrivateKey)
            .ToList();
        return new PfxFile(certificate, chain);
    }

    /// <summary>"lakeside.providers.ezehr, issued by EZEHR Issuing CA", read from the certificate itself.</summary>
    public static string Describe(string path)
    {
        if (!File.Exists(path))
        {
            return $"none: {path} is missing";
        }
        X509Certificate2 certificate = Load(path).Certificate;
        string subject = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        string issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        return $"{subject}, issued by {issuer}";
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        X509Certificate2Collection collection = [Certificate, .. Chain];
        byte[] pkcs12 = collection.ExportPkcs12(
            Pkcs12ExportPbeParameters.Pbes2Aes256Sha256,
            string.Empty
        );
        File.WriteAllBytes(path, pkcs12);
    }
}
