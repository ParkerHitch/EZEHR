using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Calls;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Spectre.Console;

namespace EZEHR.Tools.Apps;

/// <summary>
/// The Kidney Check example app. With its own certificate it reads the patient's eGFR labs (only under the grant
/// the patient turned on), checks each lab's signature itself, applies KidneyRule, and when the rule flags writes
/// one signed note back. The note's id is fixed, so running it again stores nothing new.
/// </summary>
public static class KidneyCheckApp
{
    public const string Slug = "kidney-check";

    public const string NoteId = "kidney-check";

    public const string AppName = "Kidney Check";

    public static async Task<bool> RunAsync(string baseUrl, Guid patientId)
    {
        string pfxPath = IdentityReference.Parse(Slug).RequirePfx();
        using HttpClient client = NetworkClient.Create(baseUrl, pfxPath);
        KidneyResult? result = await ReadEgfrLabsAsync(client, patientId);
        if (result is null)
        {
            return false;
        }
        return await WriteSignedNoteAsync(client, PfxFile.Load(pfxPath), patientId, result);
    }

    /// <summary>
    /// Reads the patient's eGFR labs, checks each lab's signature itself and applies KidneyRule. Null when the
    /// server refused.
    /// </summary>
    public static async Task<KidneyResult?> ReadEgfrLabsAsync(HttpClient client, Guid patientId)
    {
        ReadOutcome labs = await RecordReads.ReadAsync(
            client,
            patientId,
            RecordKind.EgfrLabs,
            null,
            "lab results only, under the grant the patient turned on"
        );
        if (!labs.Succeeded)
        {
            AppNotes.ExplainRefusal(AppName, "labs");
            return null;
        }
        IAnsiConsole console = AnsiConsole.Console;
        console.WriteLine();
        console.WriteLine(
            $"  {AppName} checks every signature itself, against its own trust anchors:"
        );
        List<(DateOnly Date, double Egfr, string Source)> points = [];
        foreach (CheckedEntry checkedEntry in SignatureCheck.Verify(labs.Entries))
        {
            if (checkedEntry.Verified is null)
            {
                console.WriteLine($"    skipped {checkedEntry.Entry.Id}: {checkedEntry.Failure}");
                continue;
            }
            JsonElement resource = checkedEntry.Verified.Resource;
            DateOnly date = DateOnly.Parse(
                resource.GetProperty("effectiveDateTime").GetString()![..10]
            );
            double egfr = resource.GetProperty("valueQuantity").GetProperty("value").GetDouble();
            string signer = checkedEntry.Verified.Signer.OrgName;
            points.Add((date, egfr, signer));
            console.WriteLine($"    {date:yyyy-MM-dd}  eGFR {egfr}  {Copy.SignedBy(signer)}");
        }
        KidneyResult result = KidneyRule.Evaluate(points);
        console.WriteLine();
        console.WriteLine(
            $"  {points.Count} eGFR results from {result.ProviderCount} providers; drop {result.PercentDrop}% from earliest to latest."
        );
        console.WriteLine(
            result.Flag ? "  The rule flags it: a note goes back." : "  The rule doesn't flag."
        );
        return result;
    }

    /// <summary>When the rule flagged, writes one signed note back.</summary>
    public static async Task<bool> WriteSignedNoteAsync(
        HttpClient client,
        PfxFile appCertificate,
        Guid patientId,
        KidneyResult result
    )
    {
        if (!result.Flag)
        {
            AnsiConsole.Console.WriteLine("  No note: the rule didn't flag.");
            return true;
        }
        return await AppNotes.WriteAsync(
            client,
            appCertificate,
            patientId,
            AppName,
            NoteId,
            Copy.KidneyNote(result.PercentDrop, result.ProviderCount)
        );
    }
}
