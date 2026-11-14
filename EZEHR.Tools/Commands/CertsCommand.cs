using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using EZCAClient.Managers;
using EZCAClient.Models;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EZEHR.Tools.Commands;

/// <summary>
/// Issues every organization and app certificate in OrgDirectory, and the broker's deliberately self-made one.
/// Writes .pki/{slug}.pfx plus .pki/anchors.pem and .pki/intermediates.pem, the public trust files the server
/// loads (copied to Security/Trust/ for the deployed site).
/// </summary>
public static class CertsCommand
{
    public const string Usage = """
          certs issue --ca local|ezca [--key ec|rsa] [--ca-id <id>] [--template-id <id>]
          certs broker
        """;

    public const int ValidityDays = 365;

    public const string BrokerCommonName = "exchange.broker.example";

    private const string EzcaUrl = "https://portal.ezca.io";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "issue")
        {
            return await IssueAsync(args[1..]);
        }
        if (args.Length > 0 && args[0] == "broker")
        {
            return WriteBroker();
        }
        Console.Error.WriteLine($"usage:\n{Usage}");
        return 1;
    }

    private static async Task<int> IssueAsync(string[] args)
    {
        string? authority = CommandLine.Option(args, "--ca");
        string keyType = CommandLine.Option(args, "--key") ?? "ec";
        if (keyType is not ("ec" or "rsa"))
        {
            Console.Error.WriteLine("--key must be ec or rsa");
            return 1;
        }

        ICertificateAuthorityService? certificateAuthority = authority switch
        {
            "local" => LocalAuthority(),
            "ezca" => await EzcaAuthorityAsync(
                CommandLine.Option(args, "--ca-id"),
                CommandLine.Option(args, "--template-id")
            ),
            _ => null,
        };
        if (certificateAuthority is null)
        {
            if (authority is not ("local" or "ezca"))
            {
                Console.Error.WriteLine($"usage:\n{Usage}");
            }
            return 1;
        }

        List<X509Certificate2> chainCertificates = [];
        foreach (string commonName in OrgDirectory.KnownOrganizations.Keys)
        {
            PfxFile issued = await IssueOneAsync(certificateAuthority, commonName, keyType);
            string path = ToolPaths.Pki($"{Slug(commonName)}.pfx");
            issued.Save(path);
            chainCertificates.AddRange(issued.Chain);
            Console.WriteLine(
                $"wrote {path}  CN={commonName}  valid until {issued.Certificate.NotAfter:yyyy-MM-dd}"
            );
        }

        // Self-signed chain members are the roots we trust; the rest are issuing CAs.
        List<X509Certificate2> distinct = chainCertificates
            .DistinctBy(certificate => certificate.Thumbprint)
            .ToList();
        WritePem(
            ToolPaths.Pki("anchors.pem"),
            distinct.Where(certificate =>
                certificate.SubjectName.RawData.SequenceEqual(certificate.IssuerName.RawData)
            )
        );
        WritePem(
            ToolPaths.Pki("intermediates.pem"),
            distinct.Where(certificate =>
                !certificate.SubjectName.RawData.SequenceEqual(certificate.IssuerName.RawData)
            )
        );
        Console.WriteLine(
            $"wrote {ToolPaths.Pki("anchors.pem")} and {ToolPaths.Pki("intermediates.pem")}"
        );
        return 0;
    }

    private static async Task<PfxFile> IssueOneAsync(
        ICertificateAuthorityService certificateAuthority,
        string commonName,
        string keyType
    )
    {
        if (keyType == "rsa")
        {
            RSA rsaKey = RSA.Create(2048);
            CertificateRequest rsaRequest = new(
                $"CN={commonName}",
                rsaKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            IssuedCertificate rsaIssued = await certificateAuthority.IssueAsync(
                rsaRequest.CreateSigningRequestPem(),
                commonName,
                ValidityDays,
                CancellationToken.None
            );
            return new PfxFile(rsaIssued.Leaf.CopyWithPrivateKey(rsaKey), rsaIssued.Chain);
        }

        ECDsa ecdsaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest ecdsaRequest = new(
            $"CN={commonName}",
            ecdsaKey,
            HashAlgorithmName.SHA256
        );
        IssuedCertificate ecdsaIssued = await certificateAuthority.IssueAsync(
            ecdsaRequest.CreateSigningRequestPem(),
            commonName,
            ValidityDays,
            CancellationToken.None
        );
        return new PfxFile(ecdsaIssued.Leaf.CopyWithPrivateKey(ecdsaKey), ecdsaIssued.Chain);
    }

    /// <summary>The fallback CA (App:CertificateAuthority:Mode=Local). Reuses .pki/local-root.pfx so re-issuing keeps the server's trust anchor.</summary>
    private static LocalCertificateAuthorityService LocalAuthority()
    {
        string rootPath = ToolPaths.Pki("local-root.pfx");
        X509Certificate2 root;
        if (File.Exists(rootPath))
        {
            root = PfxFile.Load(rootPath).Certificate;
            Console.WriteLine($"using {rootPath}");
        }
        else
        {
            root = LocalCertificateAuthorityService.CreateRoot(
                TimeProvider.System,
                "EZEHR Local Root"
            );
            new PfxFile(root, []).Save(rootPath);
            Console.WriteLine($"wrote {rootPath}");
        }
        return new LocalCertificateAuthorityService(root, TimeProvider.System);
    }

    /// <summary>Signs in with DefaultAzureCredential (az login) and picks the issuing CA by id, or the only one offered.</summary>
    private static async Task<EzcaCertificateAuthorityService?> EzcaAuthorityAsync(
        string? caId,
        string? templateId
    )
    {
        EZCAClientClass client = new(
            new HttpClient(),
            NullLogger.Instance,
            EzcaUrl,
            new DefaultAzureCredential()
        );
        AvailableCAModel[] available = await client.GetAvailableCAsAsync() ?? [];
        List<AvailableCAModel> matching = available
            .Where(ca => caId is null || ca.CAID == caId)
            .Where(ca => templateId is null || ca.TemplateID == templateId)
            .ToList();
        if (matching.Count == 1)
        {
            Console.WriteLine($"using EZCA CA {matching[0].CAFriendlyName} ({matching[0].CAID})");
            return new EzcaCertificateAuthorityService(client, matching[0]);
        }

        Console.Error.WriteLine(
            matching.Count == 0
                ? "No matching EZCA CA. Available:"
                : "More than one EZCA CA matches; pass --ca-id (and --template-id). Available:"
        );
        foreach (AvailableCAModel ca in available)
        {
            Console.Error.WriteLine(
                $"  {ca.CAFriendlyName}  --ca-id {ca.CAID} --template-id {ca.TemplateID}"
            );
        }
        return null;
    }

    /// <summary>The data broker's certificate: self-signed on purpose, so the network refuses it.</summary>
    private static int WriteBroker()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={BrokerCommonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
        );
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid(CertificateValidator.ClientAuthenticationOid)],
                false
            )
        );
        DateTimeOffset now = DateTimeOffset.UtcNow;
        X509Certificate2 certificate = request.CreateSelfSigned(
            now.AddMinutes(-5),
            now.AddDays(ValidityDays)
        );
        string path = ToolPaths.Pki("broker.pfx");
        new PfxFile(certificate, []).Save(path);
        Console.WriteLine($"wrote {path}  CN={BrokerCommonName}  (self-signed)");
        return 0;
    }

    /// <summary>"lakeside.providers.ezehr" → "lakeside"; "kidney-check.apps.ezehr" → "kidney-check".</summary>
    private static string Slug(string commonName)
    {
        return commonName.Split('.')[0];
    }

    private static void WritePem(string path, IEnumerable<X509Certificate2> certificates)
    {
        File.WriteAllText(
            path,
            string.Concat(
                certificates.Select(certificate => certificate.ExportCertificatePem() + "\n")
            )
        );
    }
}
