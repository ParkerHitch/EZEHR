using System.Security.Cryptography.X509Certificates;
using EZEHR.Domain;

namespace EZEHR.Security.Pki;

/// <summary>Maps an organization or app certificate's subject name (CN) to who it is. Unknown names resolve to nobody.</summary>
public sealed class OrgDirectory
{
    public static readonly IReadOnlyDictionary<string, Party> Known = new Dictionary<string, Party>(StringComparer.OrdinalIgnoreCase)
    {
        ["lakeside.providers.ezehr"] = Org(PartyKind.Provider, "urn:provider:lakeside", "Lakeside Urgent Care"),
        ["northwind.providers.ezehr"] = Org(PartyKind.Provider, "urn:provider:northwind", "Northwind Workplace Health"),
        ["maple.providers.ezehr"] = Org(PartyKind.Provider, "urn:provider:maple", "Maple Street Family Medicine"),
        ["kidney-check.apps.ezehr"] = Org(PartyKind.App, "urn:app:kidney-check", "Kidney Check"),
        ["platform.ezehr"] = Org(PartyKind.Platform, "urn:platform:ezehr", "EZEHR"),
    };

    public Party? Resolve(X509Certificate2 leaf) =>
        Known.TryGetValue(leaf.GetNameInfo(X509NameType.SimpleName, false), out var party) ? party : null;

    public Party? BySubjectUri(string uri) => Known.Values.FirstOrDefault(p => p.SubjectUri == uri);

    private static Party Org(PartyKind kind, string uri, string name) => new(kind, uri, name, name);
}
