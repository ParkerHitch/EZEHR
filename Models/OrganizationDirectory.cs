using System.Security.Cryptography.X509Certificates;

namespace EZEHR.Models;

/// <summary>
/// The known organizations, for lookups by certificate, platform URI or Entra tenant. The server loads one from
/// the organizations table (OrganizationManager); the tools use OrgDirectory.Demo. Anything not listed is nobody.
/// </summary>
public sealed class OrganizationDirectory(IEnumerable<Organization> organizations)
{
    private readonly IReadOnlyList<Organization> _organizations = organizations.ToList();

    /// <summary>Matches the certificate's CN, ignoring case like the DNS name it is.</summary>
    public Party? FindByCertificate(X509Certificate2 leaf)
    {
        string commonName = leaf.GetNameInfo(X509NameType.SimpleName, false);
        Organization? organization = _organizations.FirstOrDefault(candidate =>
            string.Equals(
                candidate.CertificateCommonName,
                commonName,
                StringComparison.OrdinalIgnoreCase
            )
        );
        return organization?.ToParty();
    }

    public Party? FindByUri(string uri)
    {
        return _organizations.FirstOrDefault(candidate => candidate.Uri == uri)?.ToParty();
    }

    /// <summary>The provider whose clinicians sign in through this Entra tenant, or null when none does.</summary>
    public Party? FindProviderByTenant(Guid tenantId)
    {
        return _organizations
            .FirstOrDefault(candidate =>
                candidate.Kind == PartyKind.Provider && candidate.EntraTenantId == tenantId
            )
            ?.ToParty();
    }
}
