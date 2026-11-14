using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;

namespace EZEHR.Services;

/// <summary>
/// Who each organization and app certificate belongs to, keyed by subject name (CN). The only place that builds
/// subject URIs and certificate CNs. Unknown names resolve to nobody.
/// </summary>
public static class OrgDirectory
{
    public const string KidneyCheckCommonName = "kidney-check.apps.ezehr";

    public static readonly IReadOnlyDictionary<string, Party> KnownOrganizations = new Dictionary<
        string,
        Party
    >(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderCommonName("lakeside")] = Provider("lakeside", "Lakeside Urgent Care"),
        [ProviderCommonName("northwind")] = Provider("northwind", "Northwind Workplace Health"),
        [ProviderCommonName("maple")] = Provider("maple", "Maple Street Family Medicine"),
        [ProviderCommonName("riverside")] = Provider("riverside", "Riverside Primary Care"),
        [KidneyCheckCommonName] = Organization(
            PartyKind.App,
            "urn:app:kidney-check",
            "Kidney Check"
        ),
    };

    public static Party? FindByCertificate(X509Certificate2 leaf)
    {
        string subjectName = leaf.GetNameInfo(X509NameType.SimpleName, false);
        if (KnownOrganizations.TryGetValue(subjectName, out Party? party))
        {
            return party;
        }
        return null;
    }

    public static Party? FindBySubjectUri(string uri)
    {
        return KnownOrganizations.Values.FirstOrDefault(party => party.SubjectUri == uri);
    }

    public static string ProviderCommonName(string slug)
    {
        return $"{slug}.providers.ezehr";
    }

    public static string ProviderUri(string slug)
    {
        return $"urn:provider:{slug}";
    }

    public static string ClinicianUri(string tenantId, string objectId)
    {
        return $"urn:entra:{tenantId}:{objectId}";
    }

    private static Party Provider(string slug, string name)
    {
        return Organization(PartyKind.Provider, ProviderUri(slug), name);
    }

    private static Party Organization(PartyKind kind, string uri, string name)
    {
        return new Party(kind, uri, name, name);
    }
}
