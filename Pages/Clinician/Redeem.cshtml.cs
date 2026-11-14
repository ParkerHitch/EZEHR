using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>The clinician types the patient's share code. A good code makes a pending request the patient approves.</summary>
public sealed class RedeemModel(CallerManager callerManager, GrantManager grantManager) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = "";

    public string? ErrorCopy { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        ClinicianCheck check = await callerManager.EvaluateClinicianAsync(User);
        if (!check.Ok)
        {
            return RedirectToPage("/Clinician/Index");
        }
        GrantResult result = await grantManager.RedeemShareCodeAsync(Code, check.Party!);
        if (!result.Succeeded)
        {
            ErrorCopy = Copy.ShareCodeFailed(result.Error!.Value);
            return Page();
        }
        return RedirectToPage("/Clinician/Wait", new { patientId = result.Grant!.PatientId });
    }
}
