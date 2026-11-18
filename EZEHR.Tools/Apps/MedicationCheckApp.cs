using System.Text.Json;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Calls;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Spectre.Console;

namespace EZEHR.Tools.Apps;

/// <summary>
/// The Medication Check example app: a second app on the same platform, with a wider scope than Kidney Check. With
/// its own certificate it reads the patient's medications and eGFR labs (only under the grant the patient turned
/// on), checks each signature itself, applies MedicationRule, and when the rule flags writes one signed note back.
/// </summary>
public static class MedicationCheckApp
{
    public const string Slug = "medication-check";

    public const string NoteId = "medication-check";

    public const string AppName = "Medication Check";

    public static async Task<bool> RunAsync(string baseUrl, Guid patientId)
    {
        string pfxPath = IdentityReference.Parse(Slug).RequirePfx();
        using HttpClient client = NetworkClient.Create(baseUrl, pfxPath);
        ReadOutcome medications = await RecordReads.ReadAsync(
            client,
            patientId,
            RecordKind.Medications,
            null,
            "medications, under the grant the patient turned on"
        );
        if (!medications.Succeeded)
        {
            AppNotes.ExplainRefusal(AppName, "medications");
            return false;
        }
        ReadOutcome labs = await RecordReads.ReadAsync(
            client,
            patientId,
            RecordKind.EgfrLabs,
            null,
            "lab results, under the grant the patient turned on"
        );
        if (!labs.Succeeded)
        {
            AppNotes.ExplainRefusal(AppName, "lab results");
            return false;
        }

        IAnsiConsole console = AnsiConsole.Console;
        console.WriteLine();
        console.WriteLine(
            $"  {AppName} checks every signature itself, against its own trust anchors:"
        );
        List<string> activeMedications = [];
        foreach (CheckedEntry medication in SignatureCheck.Verify(medications.Entries))
        {
            if (medication.Verified is null)
            {
                console.WriteLine($"    skipped {medication.Entry.Id}: {medication.Failure}");
                continue;
            }
            JsonElement resource = medication.Verified.Resource;
            bool active =
                !resource.TryGetProperty("status", out JsonElement status)
                || status.GetString() == "active";
            string? name = MedicationName(resource);
            if (!active || name is null)
            {
                continue;
            }
            activeMedications.Add(name);
            console.WriteLine($"    {name}  {Copy.SignedBy(medication.Verified.Signer.OrgName)}");
        }
        List<(DateOnly Date, double Egfr, string Source)> egfrPoints = [];
        foreach (CheckedEntry lab in SignatureCheck.Verify(labs.Entries))
        {
            if (lab.Verified is null)
            {
                console.WriteLine($"    skipped {lab.Entry.Id}: {lab.Failure}");
                continue;
            }
            JsonElement resource = lab.Verified.Resource;
            DateOnly date = DateOnly.Parse(
                resource.GetProperty("effectiveDateTime").GetString()![..10]
            );
            double egfr = resource.GetProperty("valueQuantity").GetProperty("value").GetDouble();
            egfrPoints.Add((date, egfr, lab.Verified.Signer.OrgName));
            console.WriteLine(
                $"    {date:yyyy-MM-dd}  eGFR {egfr}  {Copy.SignedBy(lab.Verified.Signer.OrgName)}"
            );
        }

        MedicationResult result = MedicationRule.Evaluate(activeMedications, egfrPoints);
        console.WriteLine();
        console.WriteLine(
            $"  {activeMedications.Count} active medications; eGFR drop {result.PercentDrop}% from earliest to latest."
        );
        if (!result.Flag)
        {
            console.WriteLine("  The rule doesn't flag. No note.");
            return true;
        }
        console.WriteLine("  The rule flags it: a note goes back.");
        return await AppNotes.WriteAsync(
            client,
            PfxFile.Load(pfxPath),
            patientId,
            AppName,
            NoteId,
            Copy.MedicationNote(result.Medication!, result.PercentDrop)
        );
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
}
