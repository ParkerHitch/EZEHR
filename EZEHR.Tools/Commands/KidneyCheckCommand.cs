using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;

namespace EZEHR.Tools.Commands;

/// <summary>
/// The Kidney Check example app. With its own certificate it reads the patient's eGFR labs (only under the grant
/// the patient turned on), checks each lab's signature itself, applies KidneyRule, and when the rule flags writes
/// one signed note back. The note's id is fixed, so running it again stores nothing new.
/// </summary>
public static class KidneyCheckCommand
{
    public const string Usage = """
          kidney-check run <patientId>
        """;

    public const string NoteId = "kidney-check";

    public const string AppName = "Kidney Check";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "run" || !Guid.TryParse(args[1], out Guid patientId))
        {
            Console.Error.WriteLine($"usage:\n{Usage}");
            return 1;
        }

        string pfxPath = ToolPaths.Pki("kidney-check.pfx");
        using HttpClient client = NetworkClient.Create(NetworkClient.BaseUrl, pfxPath);
        KidneyResult? result = await ReadEgfrLabsAsync(client, patientId);
        if (result is null)
        {
            return 1;
        }
        return await WriteSignedNoteAsync(client, PfxFile.Load(pfxPath), patientId, result);
    }

    /// <summary>
    /// Reads the patient's eGFR labs, checks each lab's signature itself and applies KidneyRule. Null when the
    /// server refused.
    /// </summary>
    public static async Task<KidneyResult?> ReadEgfrLabsAsync(HttpClient client, Guid patientId)
    {
        string path = $"api/network/patients/{patientId}/read-egfr-labs";
        NetworkLog.Request("GET", path, "lab results only, under the grant Sam turned on");
        using HttpResponseMessage labsResponse = await client.GetAsync(path);
        if (!labsResponse.IsSuccessStatusCode)
        {
            await NetworkLog.FailedAsync(labsResponse);
            if (labsResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                Console.WriteLine(
                    $"  {AppName} can't read this patient's labs: the patient revoked access or never turned it on."
                );
            }
            return null;
        }
        SignedEntriesResponse labs = (
            await labsResponse.Content.ReadFromJsonAsync<SignedEntriesResponse>(NetworkClient.Json)
        )!;
        NetworkLog.Succeeded(labsResponse, $"{labs.Entries.Count} signed lab results");

        Console.WriteLine();
        Console.WriteLine(
            $"  {AppName} checks every signature itself, against its own trust anchors:"
        );
        List<(DateOnly Date, double Egfr, string Source)> points = VerifiedPoints(labs.Entries);
        KidneyResult result = KidneyRule.Evaluate(points);
        Console.WriteLine();
        Console.WriteLine(
            $"  {points.Count} eGFR results from {result.ProviderCount} providers; drop {result.PercentDrop}% from earliest to latest."
        );
        Console.WriteLine(
            result.Flag ? "  The rule flags it: a note goes back." : "  The rule doesn't flag."
        );
        return result;
    }

    /// <summary>
    /// When the rule flagged, writes one signed note back. Its id is fixed, so writing it twice stores nothing new.
    /// </summary>
    public static async Task<int> WriteSignedNoteAsync(
        HttpClient client,
        PfxFile appCertificate,
        Guid patientId,
        KidneyResult result
    )
    {
        if (!result.Flag)
        {
            Console.WriteLine("  No note: the rule didn't flag.");
            return 0;
        }

        string note = Copy.KidneyNote(result.PercentDrop, result.ProviderCount);
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

    /// <summary>
    /// The labs whose signatures check out against our own copy of the network's trust anchors, dated by their
    /// clinical date (effectiveDateTime), with the signing organization as the source.
    /// </summary>
    private static List<(DateOnly Date, double Egfr, string Source)> VerifiedPoints(
        IReadOnlyList<SignedEntry> entries
    )
    {
        TrustAnchors trustAnchors = CertificateValidator.LoadTrustAnchors(
            new AppOptions
            {
                TrustAnchorsPemPath = ToolPaths.Pki("anchors.pem"),
                IntermediatesPemPath = ToolPaths.Pki("intermediates.pem"),
            }
        );
        OrganizationDirectory directory = ToolOrganizations.Load();
        List<(DateOnly Date, double Egfr, string Source)> points = [];
        foreach (SignedEntry entry in entries)
        {
            VerifiedEntry verified;
            try
            {
                verified = RecordVerifier.Verify(entry.Id, entry.Jws, trustAnchors, directory);
            }
            catch (RecordSignatureException exception)
            {
                Console.Error.WriteLine($"    skipped {entry.Id}: {exception.Message}");
                continue;
            }
            JsonElement resource = verified.Resource;
            DateOnly date = DateOnly.Parse(
                resource.GetProperty("effectiveDateTime").GetString()![..10]
            );
            double egfr = resource.GetProperty("valueQuantity").GetProperty("value").GetDouble();
            points.Add((date, egfr, verified.Signer.OrgName));
            Console.WriteLine(
                $"    {date:yyyy-MM-dd}  eGFR {egfr}  {Copy.SignedBy(verified.Signer.OrgName)}"
            );
        }
        return points;
    }
}
