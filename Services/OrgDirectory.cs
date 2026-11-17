using EZEHR.Models;

namespace EZEHR.Services;

/// <summary>
/// The only place that builds subject URIs and certificate CNs. Also holds the six demo organizations: the
/// database seeds them into the organizations table, and the tools (which have no database) use them as their
/// directory. The server's directory is the table, not this list.
/// </summary>
public static class OrgDirectory
{
    public const string KidneyCheckCommonName = "kidney-check.apps.ezehr";

    public const string KidneyCheckUri = "urn:app:kidney-check";

    public const string MedicationCheckCommonName = "medication-check.apps.ezehr";

    public const string MedicationCheckUri = "urn:app:medication-check";

    /// <summary>The CN of the platform operator's certificate. The server pins its thumbprint, not this name.</summary>
    public const string PlatformCommonName = "platform.ezehr";

    /// <summary>A provider slug: one DNS label, lowercase, so it fits both the URI and the certificate CN.</summary>
    public const string SlugPattern = "^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$";

    public static readonly IReadOnlyList<Organization> DemoOrganizations =
    [
        DemoProvider("lakeside", "Lakeside Urgent Care"),
        DemoProvider("northwind", "Northwind Workplace Health"),
        DemoProvider("maple", "Maple Street Family Medicine"),
        DemoProvider("riverside", "Riverside Primary Care"),
        new Organization(
            KidneyCheckUri,
            PartyKind.App,
            "Kidney Check",
            KidneyCheckCommonName,
            null,
            GrantScope.LabsAndNotes,
            "Looks for slow changes in your kidney lab results that no single provider would see."
        ),
        new Organization(
            MedicationCheckUri,
            PartyKind.App,
            "Medication Check",
            MedicationCheckCommonName,
            null,
            GrantScope.MedicationsLabsAndNotes,
            "Looks at your medications next to your kidney lab results, and tells you when a medicine may need a second look."
        ),
    ];

    public static readonly OrganizationDirectory Demo = new(DemoOrganizations);

    public static string ProviderCommonName(string slug)
    {
        return $"{slug}.providers.ezehr";
    }

    public static string ProviderUri(string slug)
    {
        return $"urn:provider:{slug}";
    }

    public static string AppCommonName(string slug)
    {
        return $"{slug}.apps.ezehr";
    }

    public static string AppUri(string slug)
    {
        return $"urn:app:{slug}";
    }

    public static string ClinicianUri(string tenantId, string objectId)
    {
        return $"urn:entra:{tenantId}:{objectId}";
    }

    private static Organization DemoProvider(string slug, string name)
    {
        return new Organization(
            ProviderUri(slug),
            PartyKind.Provider,
            name,
            ProviderCommonName(slug),
            null
        );
    }
}
