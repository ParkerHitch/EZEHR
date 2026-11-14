using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;

namespace EZEHR.Tools;

/// <summary>
/// The tools' directory of organizations, since they have no database: the five demo organizations plus every
/// provider `certs issue --provider` onboarded, which it keeps in .pki/organizations.json.
/// </summary>
public static class ToolOrganizations
{
    public static string FilePath
    {
        get { return ToolPaths.Pki("organizations.json"); }
    }

    public static OrganizationDirectory Load()
    {
        return new OrganizationDirectory([.. OrgDirectory.DemoOrganizations, .. Onboarded()]);
    }

    /// <summary>Remembers an onboarded organization, replacing any earlier entry with the same URI.</summary>
    public static void Add(Organization organization)
    {
        List<Organization> onboarded = Onboarded()
            .Where(existing => existing.Uri != organization.Uri)
            .Append(organization)
            .ToList();
        Directory.CreateDirectory(ToolPaths.PkiDirectory);
        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(
                onboarded,
                new JsonSerializerOptions(NetworkClient.Json) { WriteIndented = true }
            )
        );
    }

    private static IReadOnlyList<Organization> Onboarded()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<Organization>>(
                File.ReadAllText(FilePath),
                NetworkClient.Json
            ) ?? [];
    }
}
