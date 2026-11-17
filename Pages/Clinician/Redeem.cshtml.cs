using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>
/// The clinician types the patient's share code. A good code makes a pending request the patient approves. Reached
/// only through a launch from the clinician's own organization, whose id every request here carries.
/// </summary>
public sealed class RedeemModel(
    CallerManager callerManager,
    GrantManager grantManager,
    LaunchManager launchManager
) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid LaunchId { get; set; }

    [BindProperty]
    public string Code { get; set; } = "";

    public string LaunchedFrom { get; private set; } = "";

    public string? ErrorCopy { get; private set; }

    /// <summary><paramref name="error"/> is what the last attempt on this form failed with, if it did.</summary>
    public async Task<IActionResult> OnGetAsync(GrantError? error)
    {
        ClinicianCheck check = await callerManager.EvaluateClinicianAsync(User);
        if (!check.Ok)
        {
            return RedirectToPage("/Clinician/Index");
        }
        ClinicianLaunch? launch = await launchManager.FindOpenLaunchAsync(LaunchId, check.Party!);
        if (launch is null)
        {
            return RedirectToPage("/Clinician/Index");
        }
        LaunchedFrom = Copy.LaunchedFrom(launch.ProviderPatientId, launch.EncounterId);
        ErrorCopy = error is GrantError failed ? Copy.ShareCodeFailed(failed) : null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ClinicianCheck check = await callerManager.EvaluateClinicianAsync(User);
        if (!check.Ok)
        {
            return RedirectToPage("/Clinician/Index");
        }
        GrantResult result = await grantManager.RedeemShareCodeAsync(Code, check.Party!, LaunchId);
        if (result.Error == GrantError.LaunchNotOpen)
        {
            return RedirectToPage("/Clinician/Index");
        }
        if (!result.Succeeded)
        {
            return RedirectToPage(new { launchId = LaunchId, error = result.Error });
        }
        return RedirectToPage("/Clinician/Wait", new { patientId = result.Grant!.PatientId });
    }
}
