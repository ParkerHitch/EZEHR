namespace EZEHR.Domain;

/// <summary>Bound from the "App" configuration section. Holds no secrets: Key Vault access uses the managed identity.</summary>
public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>Public origin of the site, used by WebAuthn and invite links.</summary>
    public string Origin { get; set; } = "https://localhost:5001";

    public string DbPath { get; set; } = "ezehr.db";

    /// <summary>Entra tenant id → organization display name. Clinicians from other tenants are refused.</summary>
    public Dictionary<string, string> TrustedTenants { get; set; } = new();

    public CertificateAuthorityOptions CertificateAuthority { get; set; } = new();

    public EzcaOptions Ezca { get; set; } = new();

    /// <summary>Public EZCA root certificate(s), PEM, committed under Security/Trust/.</summary>
    public string TrustAnchorsPemPath { get; set; } = "Security/Trust/anchors.pem";

    /// <summary>Public EZCA issuing certificate(s), PEM, committed under Security/Trust/.</summary>
    public string IntermediatesPemPath { get; set; } = "Security/Trust/intermediates.pem";

    /// <summary>The platform's own certificate with key (signs clinician notes). On App Service: a Key Vault reference.</summary>
    public string? PlatformCertPfxBase64 { get; set; }

    public KeyWrapperOptions KeyWrapper { get; set; } = new();

    public KeyVaultOptions KeyVault { get; set; } = new();

    /// <summary>Development and tests only: the AES-256 key-encryption key used by LocalKeyWrapper.</summary>
    public string? LocalKekBase64 { get; set; }

    /// <summary>IANA time zone for "end of day" grants.</summary>
    public string TimeZone { get; set; } = "America/Indiana/Indianapolis";
}

public sealed class CertificateAuthorityOptions
{
    /// <summary>"Ezca" or "Local".</summary>
    public string Mode { get; set; } = "Ezca";
}

public sealed class EzcaOptions
{
    public string? CaId { get; set; }
    public string? TemplateId { get; set; }
}

public sealed class KeyWrapperOptions
{
    /// <summary>"KeyVault" or "Local".</summary>
    public string Mode { get; set; } = "Local";
}

public sealed class KeyVaultOptions
{
    /// <summary>Full key identifier, e.g. https://{vault}.vault.azure.net/keys/records-kek.</summary>
    public string? KeyId { get; set; }
}
