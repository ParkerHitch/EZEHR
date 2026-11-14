using System.Globalization;
using System.Text.Json;
using EZEHR.Models;

namespace EZEHR.Services;

/// <summary>
/// Turns verified entries into the combined timeline. Entries are grouped by their clinical date, read from the
/// FHIR resource, because records are signed when they are pushed and SignedAt is not the visit date.
/// </summary>
public static class TimelineBuilder
{
    // The first of these a resource has is its clinical date.
    private static readonly string[] DateFields =
    [
        "effectiveDateTime",
        "onsetDateTime",
        "recordedDate",
        "sent",
        "authoredOn",
        "issued",
        "date",
    ];

    // Within a day, the visit first, then what came of it.
    private static readonly string[] ResourceOrder =
    [
        "Encounter",
        "Condition",
        "Observation",
        "MedicationStatement",
        "Communication",
        "ServiceRequest",
    ];

    public static Timeline Build(IReadOnlyList<SignedEntry> entries)
    {
        List<TimelineItem> appNotes = [];
        List<TimelineItem> history = [];
        foreach (SignedEntry entry in entries)
        {
            TimelineItem item = ToItem(entry);
            if (entry.Signer.Kind == PartyKind.App)
            {
                appNotes.Add(item);
            }
            else
            {
                history.Add(item);
            }
        }

        List<TimelineDay> days = history
            .GroupBy(item => item.ClinicalDate)
            .OrderByDescending(day => day.Key)
            .Select(day => new TimelineDay(
                day.Key,
                day.OrderBy(item => OrderOf(item.ResourceType)).ThenBy(item => item.Title).ToList()
            ))
            .ToList();
        return new Timeline(appNotes.OrderByDescending(note => note.ClinicalDate).ToList(), days);
    }

    private static TimelineItem ToItem(SignedEntry entry)
    {
        JsonElement resource = entry.Resource;
        string? badge =
            entry.Signer.Kind != PartyKind.App && entry.WrittenBy is string author
                ? Copy.WrittenBy(author, entry.Signer.OrgName)
                : null;
        (string title, string? detail) = Describe(entry.ResourceType, resource);
        return new TimelineItem(
            entry.Id,
            ClinicalDate(resource) ?? DateOnly.FromDateTime(entry.SignedAt.UtcDateTime),
            entry.ResourceType,
            title,
            detail,
            badge ?? Copy.SignedBy(entry.Signer.OrgName)
        );
    }

    private static (string Title, string? Detail) Describe(
        string resourceType,
        JsonElement resource
    )
    {
        switch (resourceType)
        {
            case "Encounter":
                return (FirstText(resource, "type") ?? "Visit", null);
            case "Condition":
                return (Text(resource, "code") ?? "Condition", "Diagnosis");
            case "MedicationStatement":
                return (Text(resource, "medicationCodeableConcept") ?? "Medication", "Medication");
            case "Observation":
                return (Text(resource, "code") ?? "Result", ObservationValue(resource));
            case "Communication":
                return (FirstPayload(resource) ?? "Note", null);
            case "ServiceRequest":
                return (Text(resource, "code") ?? "Request", "Order");
            default:
                return (resourceType, null);
        }
    }

    private static DateOnly? ClinicalDate(JsonElement resource)
    {
        if (
            resource.TryGetProperty("period", out JsonElement period)
            && period.ValueKind == JsonValueKind.Object
            && ParseDate(period, "start") is DateOnly start
        )
        {
            return start;
        }
        foreach (string field in DateFields)
        {
            if (ParseDate(resource, field) is DateOnly date)
            {
                return date;
            }
        }
        return null;
    }

    private static DateOnly? ParseDate(JsonElement element, string field)
    {
        if (
            element.TryGetProperty(field, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: >= 10 } text
            && DateOnly.TryParseExact(
                text[..10],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly date
            )
        )
        {
            return date;
        }
        return null;
    }

    private static string? Text(JsonElement resource, string field)
    {
        if (!resource.TryGetProperty(field, out JsonElement concept))
        {
            return null;
        }
        if (concept.TryGetProperty("text", out JsonElement text))
        {
            return text.GetString();
        }
        if (
            concept.TryGetProperty("coding", out JsonElement coding)
            && coding.ValueKind == JsonValueKind.Array
            && coding.GetArrayLength() > 0
            && coding[0].TryGetProperty("display", out JsonElement display)
        )
        {
            return display.GetString();
        }
        return null;
    }

    private static string? FirstText(JsonElement resource, string field)
    {
        if (
            resource.TryGetProperty(field, out JsonElement list)
            && list.ValueKind == JsonValueKind.Array
            && list.GetArrayLength() > 0
            && list[0].TryGetProperty("text", out JsonElement text)
        )
        {
            return text.GetString();
        }
        return null;
    }

    private static string? FirstPayload(JsonElement resource)
    {
        if (
            resource.TryGetProperty("payload", out JsonElement payload)
            && payload.ValueKind == JsonValueKind.Array
            && payload.GetArrayLength() > 0
            && payload[0].TryGetProperty("contentString", out JsonElement content)
        )
        {
            return content.GetString();
        }
        return null;
    }

    /// <summary>"81 mL/min/1.73m2", or "138/88 mmHg" for a blood pressure's two components.</summary>
    private static string? ObservationValue(JsonElement resource)
    {
        if (resource.TryGetProperty("valueQuantity", out JsonElement quantity))
        {
            return Quantity(quantity);
        }
        if (
            resource.TryGetProperty("component", out JsonElement components)
            && components.ValueKind == JsonValueKind.Array
        )
        {
            List<JsonElement> quantities = components
                .EnumerateArray()
                .Where(component => component.TryGetProperty("valueQuantity", out _))
                .Select(component => component.GetProperty("valueQuantity"))
                .ToList();
            if (quantities.Count == 0)
            {
                return null;
            }
            string values = string.Join("/", quantities.Select(Number));
            string? unit = quantities[0].TryGetProperty("unit", out JsonElement unitElement)
                ? unitElement.GetString()
                : null;
            return unit is null ? values : $"{values} {unit}";
        }
        return null;
    }

    private static string Quantity(JsonElement quantity)
    {
        string number = Number(quantity);
        if (quantity.TryGetProperty("unit", out JsonElement unit))
        {
            return $"{number} {unit.GetString()}";
        }
        return number;
    }

    private static string Number(JsonElement quantity)
    {
        if (quantity.TryGetProperty("value", out JsonElement value))
        {
            return value.GetRawText();
        }
        return "";
    }

    private static int OrderOf(string resourceType)
    {
        int index = Array.IndexOf(ResourceOrder, resourceType);
        return index < 0 ? ResourceOrder.Length : index;
    }
}
