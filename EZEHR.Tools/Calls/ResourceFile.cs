using System.Text.Json;
using EZEHR.Tools.Net;

namespace EZEHR.Tools.Calls;

/// <summary>One push: the resources a provider documented on one date.</summary>
public sealed record ResourceVisit(DateOnly Date, JsonElement[] Resources);

/// <summary>
/// FHIR resources to push, from a file: the seed format ({"visits": [{"date", "resources"}]}), a JSON array of
/// resources, or a single resource. An array or a single resource is one visit, dated today.
/// </summary>
public static class ResourceFile
{
    private sealed record SeedFile(string Provider, string Organization, ResourceVisit[] Visits);

    public static IReadOnlyList<ResourceVisit> Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ToolException($"{path} not found.");
        }
        using JsonDocument document = ParseFile(path);
        JsonElement root = document.RootElement;
        IReadOnlyList<ResourceVisit> visits;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("visits", out _))
        {
            visits = root.Deserialize<SeedFile>(NetworkClient.Json)?.Visits
                ?? throw new ToolException($"{path} has no visits.");
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            visits = [new ResourceVisit(Today(), [.. root.EnumerateArray().Select(Clone)])];
        }
        else
        {
            visits = [new ResourceVisit(Today(), [Clone(root)])];
        }
        foreach (JsonElement resource in visits.SelectMany(visit => visit.Resources))
        {
            if (
                resource.ValueKind != JsonValueKind.Object
                || !resource.TryGetProperty("resourceType", out _)
            )
            {
                throw new ToolException(
                    $"{path}: every resource needs a resourceType, and this one has none: {resource.GetRawText()}"
                );
            }
        }
        return visits;
    }

    /// <summary>A demo provider's records, from the seed data shipped with the tool.</summary>
    public static IReadOnlyList<ResourceVisit> LoadSeed(string providerSlug)
    {
        string path = Path.Combine(ToolPaths.SeedDirectory, $"{providerSlug}.json");
        if (!File.Exists(path))
        {
            throw new ToolException(
                $"{providerSlug} has no seed records to push; only the demo providers do. Push your own with --file <path>."
            );
        }
        return Load(path);
    }

    private static JsonDocument ParseFile(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new ToolException($"{path} isn't JSON: {exception.Message}");
        }
    }

    private static JsonElement Clone(JsonElement element)
    {
        return element.Clone();
    }

    private static DateOnly Today()
    {
        return DateOnly.FromDateTime(DateTime.Now);
    }
}
