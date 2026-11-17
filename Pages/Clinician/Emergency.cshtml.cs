using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>
/// Break-glass. The clinician types the patient's emergency code and a reason, and the record opens for an hour
/// with no approval. The patient turned this on ahead of time, and sees who opened it, when and why.
/// </summary>
public sealed class EmergencyModel(CallerManager callerManager, GrantManager grantManager)
    : PageModel
{
    [BindProperty]
    public string Code { get; set; } = "";

    [BindProperty]
    public string Reason { get; set; } = "";

    public string? ErrorCopy { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        ClinicianCheck check = await callerManager.EvaluateClinicianAsync(User);
        if (!check.Ok)
        {
            return RedirectToPage("/Clinician/Index");
        }
        GrantResult result = await grantManager.OpenEmergencyAccessAsync(
            Code,
            Reason,
            check.Party!
        );
        if (!result.Succeeded)
        {
            ErrorCopy = Copy.EmergencyAccessFailed(result.Error!.Value);
            return Page();
        }
        return RedirectToPage("/Clinician/Record", new { patientId = result.Grant!.PatientId });
    }
}
