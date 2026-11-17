namespace EZEHR.Models;

/// <summary>
/// One row of the organizations table: a provider or app on the network. Uri is its identity on the platform,
/// CertificateCommonName the CN of its EZCA client certificate, and EntraTenantId the tenant whose clinicians it
/// vouches for (providers only). Apps only: AppScope is the most a patient can allow the app (null means
/// LabsAndNotes, the narrowest), and AppDescription is what the patient reads before turning it on.
/// </summary>
public sealed record Organization(
    string Uri,
    PartyKind Kind,
    string DisplayName,
    string CertificateCommonName,
    Guid? EntraTenantId,
    GrantScope? AppScope = null,
    string? AppDescription = null
)
{
    /// <summary>What a grant for this app covers. An app that declares nothing gets the narrowest scope.</summary>
    public GrantScope GrantedAppScope()
    {
        return AppScope ?? GrantScope.LabsAndNotes;
    }

    public Party ToParty()
    {
        return new Party(Kind, Uri, DisplayName, DisplayName, Uri);
    }
}
