using System.Text.Json;
using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>
/// The patient's combined record: app notes on top, then every provider's entries by visit date. htmx polls
/// Timeline every 2 s, so a record a provider pushes shows up without a refresh.
/// </summary>
public sealed class HomeModel(CallerManager callerManager, RecordManager recordManager) : PageModel
{
    public PatientHome Home { get; private set; } = new("", new Timeline([], []), "");

    public async Task<IActionResult> OnGetAsync()
    {
        Guid patientId = callerManager.PatientIdFrom(User)!.Value;
        Home = await recordManager.ReadPatientHomeAsync(patientId);
        return Page();
    }

    public async Task<IActionResult> OnGetTimelineAsync(string? version)
    {
        Guid patientId = callerManager.PatientIdFrom(User)!.Value;
        PatientHome? home = await recordManager.ReadPatientHomeIfChangedAsync(patientId, version);
        if (home is null)
        {
            return new NoContentResult();
        }
        return Partial("_HomeTimeline", home);
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        Guid patientId = callerManager.PatientIdFrom(User)!.Value;
        ExportPatientRecordResult export = await recordManager.ExportPatientRecordAsync(patientId);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            export.Bundle,
            new JsonSerializerOptions { WriteIndented = true }
        );
        return File(json, "application/fhir+json", export.FileName);
    }
}
