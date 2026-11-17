using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;

namespace EZEHR.Tools.Commands;

/// <summary>
/// The Medication Check example app: a second app on the same platform, with a wider scope than Kidney Check. With
/// its own certificate it reads the patient's medications and eGFR labs (only under the grant the patient turned
/// on), checks each signature itself, applies MedicationRule, and when the rule flags writes one signed note back.
/// The note's id is fixed, so running it again stores nothing new.
/// </summary>
public static class MedicationCheckCommand
{
    public const string Usage = """
          medication-check run <patientId>
        """;

    public const string NoteId = "medication-check";

    public const string AppName = "Medication Check";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "run" || !Guid.TryParse(args[1], out Guid patientId))
        {
            Console.Error.WriteLine($"usage:\n{Usage}");
            return 1;
        }

        string pfxPath = ToolPaths.Pki("medication-check.pfx");
        using HttpClient client = NetworkClient.Create(NetworkClient.BaseUrl, pfxPath);
        IReadOnlyList<SignedEntry>? medications = await ReadAsync(
            client,
            $"api/network/patients/{patientId}/read-medications",
            "medications"
        );
        if (medications is null)
        {
            return 1;
        }
        IReadOnlyList<SignedEntry>? labs = await ReadAsync(
            client,
            $"api/network/patients/{patientId}/read-egfr-labs",
            "lab results"
        );
        if (labs is null)
        {
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  {AppName} checks every signature itself, against its own trust anchors:"
        );
        List<string> activeMedications = [];
        foreach (VerifiedEntry medication in Verified(medications))
        {
            JsonElement resource = medication.Resource;
            bool active =
                !resource.TryGetProperty("status", out JsonElement status)
                || status.GetString() == "active";
            string? name = MedicationName(resource);
            if (!active || name is null)
            {
                continue;
            }
            activeMedications.Add(name);
            Console.WriteLine($"    {name}  {Copy.SignedBy(medication.Signer.OrgName)}");
        }
        List<(DateOnly Date, double Egfr, string Source)> egfrPoints = [];
        foreach (VerifiedEntry lab in Verified(labs))
        {
            JsonElement resource = lab.Resource;
            DateOnly date = DateOnly.Parse(
                resource.GetProperty("effectiveDateTime").GetString()![..10]
            );
            double egfr = resource.GetProperty("valueQuantity").GetProperty("value").GetDouble();
            egfrPoints.Add((date, egfr, lab.Signer.OrgName));
            Console.WriteLine(
                $"    {date:yyyy-MM-dd}  eGFR {egfr}  {Copy.SignedBy(lab.Signer.OrgName)}"
            );
        }

        MedicationResult result = MedicationRule.Evaluate(activeMedications, egfrPoints);
        Console.WriteLine();
        Console.WriteLine(
            $"  {activeMedications.Count} active medications; eGFR drop {result.PercentDrop}% from earliest to latest."
        );
        if (!result.Flag)
        {
            Console.WriteLine("  The rule doesn't flag. No note.");
            return 0;
        }
        Console.WriteLine("  The rule flags it: a note goes back.");
        return await WriteSignedNoteAsync(client, PfxFile.Load(pfxPath), patientId, result);
    }

    /// <summary>One scoped read. Null when the server refused.</summary>
    private static async Task<IReadOnlyList<SignedEntry>?> ReadAsync(
        HttpClient client,
        string path,
        string what
    )
    {
        NetworkLog.Request("GET", path, $"{what}, under the grant Sam turned on");
        using HttpResponseMessage response = await client.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            await NetworkLog.FailedAsync(response);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                Console.WriteLine(
                    $"  {AppName} can't read this patient's {what}: the patient revoked access or never turned it on."
                );
            }
            return null;
        }
        SignedEntriesResponse entries = (
            await response.Content.ReadFromJsonAsync<SignedEntriesResponse>(NetworkClient.Json)
        )!;
        NetworkLog.Succeeded(response, $"{entries.Entries.Count} signed {what}");
        return entries.Entries;
    }

    /// <summary>The entries whose signatures check out against our own copy of the network's trust anchors.</summary>
    private static List<VerifiedEntry> Verified(IReadOnlyList<SignedEntry> entries)
    {
        TrustAnchors trustAnchors = CertificateValidator.LoadTrustAnchors(
            new AppOptions
            {
                TrustAnchorsPemPath = ToolPaths.Pki("anchors.pem"),
                IntermediatesPemPath = ToolPaths.Pki("intermediates.pem"),
            }
        );
        OrganizationDirectory directory = ToolOrganizations.Load();
        List<VerifiedEntry> verified = [];
        foreach (SignedEntry entry in entries)
        {
            try
            {
                verified.Add(RecordVerifier.Verify(entry.Id, entry.Jws, trustAnchors, directory));
            }
            catch (RecordSignatureException exception)
            {
                Console.Error.WriteLine($"    skipped {entry.Id}: {exception.Message}");
            }
        }
        return verified;
    }

    /// <summary>The medication as its record names it: the free text, or else the first coding's display.</summary>
    private static string? MedicationName(JsonElement resource)
    {
        if (!resource.TryGetProperty("medicationCodeableConcept", out JsonElement concept))
        {
            return null;
        }
        if (concept.TryGetProperty("text", out JsonElement text))
        {
            return text.GetString();
        }
        if (
            concept.TryGetProperty("coding", out JsonElement coding)
            && coding.GetArrayLength() > 0
            && coding[0].TryGetProperty("display", out JsonElement display)
        )
        {
            return display.GetString();
        }
        return null;
    }

    private static async Task<int> WriteSignedNoteAsync(
        HttpClient client,
        PfxFile appCertificate,
        Guid patientId,
        MedicationResult result
    )
    {
        string note = Copy.MedicationNote(result.Medication!, result.PercentDrop);
        JsonElement communication = JsonSerializer.SerializeToElement(
            new
            {
                resourceType = "Communication",
                id = NoteId,
                status = "completed",
                subject = new { reference = $"Patient/{patientId}" },
                sent = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
                sender = new { display = AppName },
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
        NetworkLog.Request("POST", path, $"one note, signed by {AppName}");
        using HttpResponseMessage noteResponse = await NetworkClient.PostJsonAsync(
            client,
            path,
            new WriteSignedNoteRequest(jws)
        );
        if (!noteResponse.IsSuccessStatusCode)
        {
            await NetworkLog.FailedAsync(noteResponse);
            return 1;
        }
        WriteSignedNoteResponse written = (
            await noteResponse.Content.ReadFromJsonAsync<WriteSignedNoteResponse>(
                NetworkClient.Json
            )
        )!;
        NetworkLog.Succeeded(noteResponse, written.Stored ? "stored" : "already stored");
        Console.WriteLine($"  \"{note}\"");
        return 0;
    }
}
