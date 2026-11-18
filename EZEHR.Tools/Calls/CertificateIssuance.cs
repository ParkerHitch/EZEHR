using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using EZCAClient.Managers;
using EZCAClient.Models;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace EZEHR.Tools.Calls;

/// <summary>
/// Issues certificates and writes them to .pki/{slug}.pfx, plus .pki/anchors.pem and .pki/intermediates.pem, the
/// public trust files the server loads (copied to Security/Trust/ for the deployed site). The CA is the local test
/// CA, or EZCA as whoever is signed in with az login. Only the CA is called, never EZEHR.
/// </summary>
public static class CertificateIssuance
{
    public const int ValidityDays = 365;

    public const string BrokerCommonName = "exchange.broker.example";

    private const string EzcaUrl = "https://portal.ezca.io";

    /// <summary>Any Azure scope gives a token naming the signed-in account; this one works without a subscription.</summary>
    private const string AccountTokenScope = "https://management.azure.com/.default";

    /// <summary>Every demo organization and app in OrgDirectory, and the trust files for their chain.</summary>
    public static async Task IssueDemoOrganizationsAsync(CertificateAuthoritySettings settings)
    {
        ICertificateAuthorityService certificateAuthority = await CreateAuthorityAsync(
            settings,
            new DefaultAzureCredential()
        );
        // The demo names were registered on the CA at setup.
        List<X509Certificate2> chainCertificates = [];
        foreach (Organization organization in OrgDirectory.DemoOrganizations)
        {
            PfxFile issued = await IssueAndSaveAsync(
                certificateAuthority,
                organization.CertificateCommonName,
                settings.KeyType
            );
            chainCertificates.AddRange(issued.Chain);
        }
        WriteTrustFiles(chainCertificates);
    }

    /// <summary>The platform operator's certificate. Returns its SHA-256 thumbprint, which the server pins.</summary>
    public static async Task<string> IssuePlatformAsync(CertificateAuthoritySettings settings)
    {
        await IssueOrganizationAsync(settings, OrgDirectory.PlatformCommonName);
        return PfxFile
            .Load(ToolPaths.Pki($"{Slug(OrgDirectory.PlatformCommonName)}.pfx"))
            .Certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>Registers the CN on the EZCA CA (when it's new there), issues its certificate and saves it to .pki/.</summary>
    public static async Task IssueOrganizationAsync(
        CertificateAuthoritySettings settings,
        string commonName
    )
    {
        DefaultAzureCredential credential = new();
        ICertificateAuthorityService certificateAuthority = await CreateAuthorityAsync(
            settings,
            credential
        );
        if (certificateAuthority is EzcaCertificateAuthorityService ezca)
        {
            await RegisterOnEzcaAsync(ezca, credential, commonName);
        }
        PfxFile issued = await IssueAndSaveAsync(
            certificateAuthority,
            commonName,
            settings.KeyType
        );
        WriteTrustFiles(issued.Chain);
    }

    /// <summary>The data broker's certificate: self-signed on purpose, so the network refuses it.</summary>
    public static void WriteBroker()
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
        AnsiConsole.Console.WriteLine($"wrote {path}  CN={BrokerCommonName}  (self-signed)");
    }

    private static async Task<ICertificateAuthorityService> CreateAuthorityAsync(
        CertificateAuthoritySettings settings,
        TokenCredential credential
    )
    {
        if (settings.Authority == "local")
        {
            return LocalAuthority();
        }
        return await EzcaAuthorityAsync(credential, settings.CaId, settings.TemplateId);
    }

    /// <summary>EZCA only issues for names registered on the CA, owned by the signed-in account.</summary>
    private static async Task RegisterOnEzcaAsync(
        EzcaCertificateAuthorityService ezca,
        TokenCredential credential,
        string commonName
    )
    {
        (string objectId, string name) = await SignedInAccountAsync(credential);
        bool registered = await ezca.EnsureDomainRegisteredAsync(commonName, objectId, name);
        AnsiConsole.Console.WriteLine(
            registered
                ? $"  EZCA: registered {commonName} on the CA, owned by {name}"
                : $"  EZCA: {commonName} is already registered on the CA"
        );
    }

    private static async Task<PfxFile> IssueAndSaveAsync(
        ICertificateAuthorityService certificateAuthority,
        string commonName,
        string keyType
    )
    {
        PfxFile issued = await IssueOneAsync(certificateAuthority, commonName, keyType);
        string path = ToolPaths.Pki($"{Slug(commonName)}.pfx");
        issued.Save(path);
        string issuer = issued.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        AnsiConsole.Console.WriteLine(
            $"  issued CN={commonName} by {issuer}, valid until {issued.Certificate.NotAfter:yyyy-MM-dd}"
        );
        return issued;
    }

    /// <summary>The object id and name of whoever DefaultAzureCredential signs in as, read from an access token.</summary>
    private static async Task<(string ObjectId, string Name)> SignedInAccountAsync(
        TokenCredential credential
    )
    {
        AccessToken token = await credential.GetTokenAsync(
            new TokenRequestContext([AccountTokenScope]),
            CancellationToken.None
        );
        string payload = token.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using JsonDocument claims = JsonDocument.Parse(
            Convert.FromBase64String(
                payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
            )
        );
        string objectId = claims.RootElement.GetProperty("oid").GetString()!;
        string name = Claim(claims, "upn")
            ?? Claim(claims, "unique_name")
            ?? Claim(claims, "name")
            ?? objectId;
        return (objectId, name);
    }

    private static string? Claim(JsonDocument claims, string name)
    {
        return claims.RootElement.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;
    }

    /// <summary>Self-signed chain members are the roots we trust; the rest are issuing CAs.</summary>
    private static void WriteTrustFiles(IEnumerable<X509Certificate2> chainCertificates)
    {
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
        AnsiConsole.Console.WriteLine(
            $"  wrote {ToolPaths.Pki("anchors.pem")} and {ToolPaths.Pki("intermediates.pem")}"
        );
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
            AnsiConsole.Console.WriteLine($"  using {rootPath}");
        }
        else
        {
            root = LocalCertificateAuthorityService.CreateRoot(
                TimeProvider.System,
                "EZEHR Local Root"
            );
            new PfxFile(root, []).Save(rootPath);
            AnsiConsole.Console.WriteLine($"wrote {rootPath}");
        }
        return new LocalCertificateAuthorityService(root, TimeProvider.System);
    }

    /// <summary>Signs in with DefaultAzureCredential (az login) and picks the issuing CA by id, or the only one offered.</summary>
    private static async Task<EzcaCertificateAuthorityService> EzcaAuthorityAsync(
        TokenCredential credential,
        string? caId,
        string? templateId
    )
    {
        EZCAClientClass client = new(new HttpClient(), NullLogger.Instance, EzcaUrl, credential);
        AvailableCAModel[] available = await client.GetAvailableCAsAsync() ?? [];
        List<AvailableCAModel> matching = available
            .Where(ca => caId is null || ca.CAID == caId)
            .Where(ca => templateId is null || ca.TemplateID == templateId)
            .ToList();
        if (matching.Count == 1)
        {
            AnsiConsole.Console.WriteLine(
                $"  EZCA: using CA {matching[0].CAFriendlyName} ({matching[0].CAID})"
            );
            return new EzcaCertificateAuthorityService(client, matching[0]);
        }
        string problem =
            matching.Count == 0
                ? "No matching EZCA CA. Available:"
                : "More than one EZCA CA matches; pass --ca-id (and --template-id). Available:";
        IEnumerable<string> choices = available.Select(ca =>
            $"  {ca.CAFriendlyName}  --ca-id {ca.CAID} --template-id {ca.TemplateID}"
        );
        throw new ToolException(string.Join("\n", [problem, .. choices]));
    }

    /// <summary>"lakeside.providers.ezehr" → "lakeside"; "kidney-check.apps.ezehr" → "kidney-check".</summary>
    private static string Slug(string commonName)
    {
        return commonName.Split('.')[0];
    }

    private static void WritePem(string path, IEnumerable<X509Certificate2> certificates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            string.Concat(
                certificates.Select(certificate => certificate.ExportCertificatePem() + "\n")
            )
        );
    }
}
