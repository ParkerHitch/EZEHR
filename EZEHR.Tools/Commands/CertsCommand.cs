using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using EZCAClient.Managers;
using EZCAClient.Models;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace EZEHR.Tools.Commands;

/// <summary>
/// Issues certificates and writes them to .pki/{slug}.pfx, plus .pki/anchors.pem and .pki/intermediates.pem, the
/// public trust files the server loads (copied to Security/Trust/ for the deployed site):
/// - with no flag, every demo organization and app in OrgDirectory;
/// - --platform, the platform operator's certificate, whose thumbprint the server pins;
/// - --provider, onboards a new provider: registers its name on the EZCA CA, issues its certificate, and adds it to
///   EZEHR through register-provider with the platform certificate.
/// Also the broker's deliberately self-made one.
/// </summary>
public static class CertsCommand
{
    public const string Usage = """
          certs issue [--ca local|ezca] [--key ec|rsa] [--ca-id <id>] [--template-id <id>]
              [--platform | --provider <slug> --name "<display name>" [--invite <providerPatientId>]]
          certs broker
        """;

    public const int ValidityDays = 365;

    public const string BrokerCommonName = "exchange.broker.example";

    private const string EzcaUrl = "https://portal.ezca.io";

    /// <summary>Any Azure scope gives a token naming the signed-in account; this one works without a subscription.</summary>
    private const string AccountTokenScope = "https://management.azure.com/.default";

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
        string authority = CommandLine.Option(args, "--ca") ?? "local";
        string keyType = CommandLine.Option(args, "--key") ?? "ec";
        bool platform = args.Contains("--platform");
        string? providerSlug = CommandLine.Option(args, "--provider");
        string? providerName = CommandLine.Option(args, "--name");
        string? inviteFor = CommandLine.Option(args, "--invite");
        if (keyType is not ("ec" or "rsa"))
        {
            Console.Error.WriteLine("--key must be ec or rsa");
            return 1;
        }
        if (authority is not ("local" or "ezca") || (platform && providerSlug is not null))
        {
            Console.Error.WriteLine($"usage:\n{Usage}");
            return 1;
        }
        if (
            providerSlug is not null
            && (
                !Regex.IsMatch(providerSlug, OrgDirectory.SlugPattern)
                || string.IsNullOrWhiteSpace(providerName)
            )
        )
        {
            Console.Error.WriteLine(
                "--provider takes a lowercase slug (letters, digits, hyphens) and needs --name \"<display name>\""
            );
            return 1;
        }
        if (providerSlug is not null && !File.Exists(PlatformPfxPath))
        {
            Console.Error.WriteLine(
                $"{PlatformPfxPath} not found: run `certs issue --ca {authority} --platform` first."
            );
            return 1;
        }

        string? caId = CommandLine.Option(args, "--ca-id");
        string? templateId = CommandLine.Option(args, "--template-id");
        if (providerSlug is not null)
        {
            int issuedExitCode = await IssueProviderCertificateAsync(
                authority,
                providerSlug,
                keyType,
                caId,
                templateId
            );
            if (issuedExitCode != 0)
            {
                return issuedExitCode;
            }
            int registeredExitCode = await RegisterProviderAsync(providerSlug, providerName!);
            if (registeredExitCode != 0)
            {
                return registeredExitCode;
            }
            if (inviteFor is null)
            {
                Console.WriteLine(
                    $"next: ezehr-tools provider invite {providerSlug} <providerPatientId>"
                );
                return 0;
            }
            Console.WriteLine();
            return await ProviderCommand.RunAsync(["invite", providerSlug, inviteFor]);
        }

        DefaultAzureCredential credential = new();
        ICertificateAuthorityService? certificateAuthority = await CreateAuthorityAsync(
            authority,
            credential,
            caId,
            templateId
        );
        if (certificateAuthority is null)
        {
            return 1;
        }

        IReadOnlyList<string> commonNames = platform
            ? [OrgDirectory.PlatformCommonName]
            : OrgDirectory
                .DemoOrganizations.Select(organization => organization.CertificateCommonName)
                .ToList();

        // The demo names were registered on the CA at setup; the platform's is registered here.
        if (certificateAuthority is EzcaCertificateAuthorityService ezca && platform)
        {
            await RegisterOnEzcaAsync(ezca, credential, OrgDirectory.PlatformCommonName);
        }

        List<X509Certificate2> chainCertificates = [];
        PfxFile? lastIssued = null;
        foreach (string commonName in commonNames)
        {
            lastIssued = await IssueAndSaveAsync(certificateAuthority, commonName, keyType);
            chainCertificates.AddRange(lastIssued.Chain);
        }
        WriteTrustFiles(chainCertificates);

        if (platform)
        {
            string thumbprint = lastIssued!.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
            Console.WriteLine();
            Console.WriteLine("Pin it on the server, then restart it:");
            Console.WriteLine(
                $"  App Service setting:  App__PlatformCertificateThumbprint = {thumbprint}"
            );
            Console.WriteLine(
                $"  locally, in the server's shell:  export App__PlatformCertificateThumbprint={thumbprint}"
            );
            return 0;
        }
        return 0;
    }

    /// <summary>
    /// A new provider's first step: EZCA registers its name on the CA (when it's new there) and issues its client
    /// certificate, saved to .pki/{slug}.pfx. EZEHR doesn't know it yet: that's RegisterProviderAsync.
    /// </summary>
    public static async Task<int> IssueProviderCertificateAsync(
        string authority,
        string slug,
        string keyType,
        string? caId,
        string? templateId
    )
    {
        DefaultAzureCredential credential = new();
        ICertificateAuthorityService? certificateAuthority = await CreateAuthorityAsync(
            authority,
            credential,
            caId,
            templateId
        );
        if (certificateAuthority is null)
        {
            return 1;
        }
        string commonName = OrgDirectory.ProviderCommonName(slug);
        if (certificateAuthority is EzcaCertificateAuthorityService ezca)
        {
            await RegisterOnEzcaAsync(ezca, credential, commonName);
        }
        PfxFile issued = await IssueAndSaveAsync(certificateAuthority, commonName, keyType);
        WriteTrustFiles(issued.Chain);
        return 0;
    }

    private static string PlatformPfxPath
    {
        get { return ToolPaths.Pki($"{Slug(OrgDirectory.PlatformCommonName)}.pfx"); }
    }

    /// <summary>Adds the provider to EZEHR with the platform certificate; already there counts as done.</summary>
    public static async Task<int> RegisterProviderAsync(string slug, string displayName)
    {
        const string path = "api/network/register-provider";
        using HttpClient client = NetworkClient.Create(NetworkClient.BaseUrl, PlatformPfxPath);
        NetworkLog.Request(
            "POST",
            path,
            $"{displayName}, certificate CN {OrgDirectory.ProviderCommonName(slug)}"
        );
        using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
            client,
            path,
            new RegisterProviderRequest(slug, displayName)
        );
        if (response.StatusCode == HttpStatusCode.Created)
        {
            NetworkLog.Succeeded(
                response,
                $"{displayName} is on the network as {OrgDirectory.ProviderUri(slug)}"
            );
        }
        else if (response.StatusCode == HttpStatusCode.Conflict)
        {
            await NetworkLog.FailedAsync(response);
            Console.WriteLine($"  {OrgDirectory.ProviderUri(slug)} was already on the network.");
        }
        else
        {
            await NetworkLog.FailedAsync(response);
            return 1;
        }
        ToolOrganizations.Add(
            new Organization(
                OrgDirectory.ProviderUri(slug),
                PartyKind.Provider,
                displayName,
                OrgDirectory.ProviderCommonName(slug),
                null
            )
        );
        return 0;
    }

    private static async Task<ICertificateAuthorityService?> CreateAuthorityAsync(
        string authority,
        TokenCredential credential,
        string? caId,
        string? templateId
    )
    {
        if (authority == "local")
        {
            return LocalAuthority();
        }
        return await EzcaAuthorityAsync(credential, caId, templateId);
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
        Console.WriteLine(
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
        Console.WriteLine(
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
        Console.WriteLine(
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
            Console.WriteLine($"  using {rootPath}");
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
            Console.WriteLine(
                $"  EZCA: using CA {matching[0].CAFriendlyName} ({matching[0].CAID})"
            );
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
