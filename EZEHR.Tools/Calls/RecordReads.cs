using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EZEHR.Models;
using EZEHR.Tools.Net;
using Spectre.Console;

namespace EZEHR.Tools.Calls;

/// <summary>The three reads of a patient's record, named after their endpoints (read-egfr-labs, ...).</summary>
public enum RecordKind
{
    EgfrLabs,
    Medications,
    FullRecord,
}

/// <summary>What the server answered: the status, its deny reason when it refused, and the entries when it didn't.</summary>
public sealed record ReadOutcome(
    HttpStatusCode Status,
    string? DenyReason,
    IReadOnlyList<SignedEntry> Entries,
    string Body
)
{
    public bool Succeeded
    {
        get { return (int)Status is >= 200 and < 300; }
    }

    /// <summary>"200 OK", or "403 ScopeDenied".</summary>
    public string Describe()
    {
        return DenyReason is null ? $"{(int)Status} {Status}" : $"{(int)Status} {DenyReason}";
    }
}

/// <summary>A read of a patient's record, as whoever the client's certificate says.</summary>
public static class RecordReads
{
    public static readonly IReadOnlyDictionary<string, RecordKind> Names = new Dictionary<
        string,
        RecordKind
    >
    {
        ["egfr-labs"] = RecordKind.EgfrLabs,
        ["medications"] = RecordKind.Medications,
        ["full-record"] = RecordKind.FullRecord,
    };

    public static string PathFor(Guid patientId, RecordKind kind, string? purpose)
    {
        string path = $"api/network/patients/{patientId}/read-{Name(kind)}";
        return purpose is null ? path : $"{path}?purpose={Uri.EscapeDataString(purpose)}";
    }

    public static string Name(RecordKind kind)
    {
        return Names.Single(pair => pair.Value == kind).Key;
    }

    /// <summary>Sends the read and prints both lines. A refusal is an outcome, not an exception.</summary>
    public static async Task<ReadOutcome> ReadAsync(
        HttpClient client,
        Guid patientId,
        RecordKind kind,
        string? purpose,
        string detail
    )
    {
        string path = PathFor(patientId, kind, purpose);
        NetworkLog.Request("GET", path, detail);
        using HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            NetworkLog.WriteInColor(
                Color.Red,
                $"  ← {(int)response.StatusCode} {response.ReasonPhrase}   {body}".TrimEnd()
            );
            return new ReadOutcome(response.StatusCode, DenyReasonIn(body), [], body);
        }
        SignedEntriesResponse entries = JsonSerializer.Deserialize<SignedEntriesResponse>(
            body,
            NetworkClient.Json
        ) ?? new SignedEntriesResponse([]);
        NetworkLog.Succeeded(response, $"{entries.Entries.Count} signed {Noun(kind)}");
        return new ReadOutcome(response.StatusCode, null, entries.Entries, body);
    }

    /// <summary>The date a resource is about, from whichever FHIR date field it has; empty when none.</summary>
    public static string ClinicalDate(JsonElement resource)
    {
        string[] dateFields =
        [
            "effectiveDateTime",
            "authoredOn",
            "recordedDate",
            "sent",
            "date",
            "issued",
        ];
        foreach (string field in dateFields)
        {
            if (
                resource.TryGetProperty(field, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
            )
            {
                string text = value.GetString()!;
                return text.Length >= 10 ? text[..10] : text;
            }
        }
        if (
            resource.TryGetProperty("period", out JsonElement period)
            && period.TryGetProperty("start", out JsonElement start)
        )
        {
            string text = start.GetString() ?? "";
            return text.Length >= 10 ? text[..10] : text;
        }
        return "";
    }

    private static string Noun(RecordKind kind)
    {
        return kind switch
        {
            RecordKind.EgfrLabs => "lab results",
            RecordKind.Medications => "medications",
            _ => "entries",
        };
    }

    /// <summary>The server's {"error": "ScopeDenied"}, or null for any other body.</summary>
    private static string? DenyReasonIn(string body)
    {
        try
        {
            ErrorResponse? error = JsonSerializer.Deserialize<ErrorResponse>(
                body,
                NetworkClient.Json
            );
            return error?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
