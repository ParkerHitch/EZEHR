namespace EZEHR.Models;

/// <summary>
/// One row of the organizations table: a provider or app on the network. Uri is its identity on the platform,
/// CertificateCommonName the CN of its EZCA client certificate, and EntraTenantId the tenant whose clinicians it
/// vouches for (providers only).
/// </summary>
public sealed record Organization(
    string Uri,
    PartyKind Kind,
    string DisplayName,
    string CertificateCommonName,
    Guid? EntraTenantId
)
{
    public Party ToParty()
    {
        return new Party(Kind, Uri, DisplayName, DisplayName, Uri);
    }
}
