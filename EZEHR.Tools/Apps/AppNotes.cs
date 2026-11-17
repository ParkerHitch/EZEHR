using System.Net.Http.Json;
using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;
using Spectre.Console;

namespace EZEHR.Tools.Apps;

/// <summary>An app writing its one signed note back to the patient's record. The note's id is fixed per app.</summary>
public static class AppNotes
{
    public static async Task<bool> WriteAsync(
        HttpClient client,
        PfxFile appCertificate,
        Guid patientId,
        string appName,
        string noteId,
        string note
    )
    {
        JsonElement communication = JsonSerializer.SerializeToElement(
            new
            {
                resourceType = "Communication",
                id = noteId,
                status = "completed",
                subject = new { reference = $"Patient/{patientId}" },
                sent = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
                sender = new { display = appName },
                payload = new[] { new { contentString = note } },
            }
        );
        string jws = RecordSigner.Sign(
            communication,
            appCertificate.Certificate,
            appCertificate.Chain,
            DateTimeOffset.UtcNow
        );
        string path = $"api/network/patients/{patientId}/write-signed-note";
        NetworkLog.Request("POST", path, $"one note, signed by {appName}");
        using HttpResponseMessage noteResponse = await NetworkClient.PostJsonAsync(
            client,
            path,
            new WriteSignedNoteRequest(jws)
        );
        if (!noteResponse.IsSuccessStatusCode)
        {
            await NetworkLog.FailedAsync(noteResponse);
            return false;
        }
        WriteSignedNoteResponse written = (
            await noteResponse.Content.ReadFromJsonAsync<WriteSignedNoteResponse>(
                NetworkClient.Json
            )
        )!;
        NetworkLog.Succeeded(noteResponse, written.Stored ? "stored" : "already stored");
        AnsiConsole.Console.WriteLine($"  \"{note}\"");
        return true;
    }

    /// <summary>The hint after a refused read: why an app usually can't read.</summary>
    public static void ExplainRefusal(string appName, string what)
    {
        AnsiConsole.Console.WriteLine(
            $"  {appName} can't read this patient's {what}: the patient revoked access or never turned it on."
        );
    }
}
