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
        PfxFile appCertificate = PfxFile.Load(pfxPath);
        using HttpClient client = NetworkClient.Create(NetworkClient.BaseUrl, pfxPath);

        using HttpResponseMessage labsResponse = await client.GetAsync(
            $"api/network/patients/{patientId}/read-egfr-labs"
        );
        if (labsResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            Console.WriteLine(
                $"{AppName} can't read this patient's labs: the patient revoked access or never turned it on."
            );
            Console.WriteLine(await NetworkClient.DescribeAsync(labsResponse));
            return 1;
        }
        if (!labsResponse.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(await NetworkClient.DescribeAsync(labsResponse));
            return 1;
        }
        SignedEntriesResponse labs = (
            await labsResponse.Content.ReadFromJsonAsync<SignedEntriesResponse>(NetworkClient.Json)
        )!;

        List<(DateOnly Date, double Egfr, string Source)> points = VerifiedPoints(labs.Entries);
        KidneyResult result = KidneyRule.Evaluate(points);
        Console.WriteLine(
            $"{points.Count} eGFR results from {result.ProviderCount} providers; drop {result.PercentDrop}% from earliest to latest."
        );
        if (!result.Flag)
        {
            Console.WriteLine("No note: the rule didn't flag.");
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
        using HttpResponseMessage noteResponse = await NetworkClient.PostJsonAsync(
            client,
            $"api/network/patients/{patientId}/write-signed-note",
            new WriteSignedNoteRequest(jws)
        );
        if (!noteResponse.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(await NetworkClient.DescribeAsync(noteResponse));
            return 1;
        }
        WriteSignedNoteResponse written = (
            await noteResponse.Content.ReadFromJsonAsync<WriteSignedNoteResponse>(
                NetworkClient.Json
            )
        )!;
        Console.WriteLine(written.Stored ? $"Wrote note: {note}" : "Note already written.");
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
        List<(DateOnly Date, double Egfr, string Source)> points = [];
        foreach (SignedEntry entry in entries)
        {
            VerifiedEntry verified;
            try
            {
                verified = RecordVerifier.Verify(entry.Id, entry.Jws, trustAnchors);
            }
            catch (RecordSignatureException exception)
            {
                Console.Error.WriteLine($"skipped {entry.Id}: {exception.Message}");
                continue;
            }
            JsonElement resource = verified.Resource;
            DateOnly date = DateOnly.Parse(
                resource.GetProperty("effectiveDateTime").GetString()![..10]
            );
            double egfr = resource.GetProperty("valueQuantity").GetProperty("value").GetDouble();
            points.Add((date, egfr, verified.Signer.OrgName));
            Console.WriteLine(
                $"  {date:yyyy-MM-dd}  eGFR {egfr}  {Copy.SignedBy(verified.Signer.OrgName)}"
            );
        }
        return points;
    }
}
