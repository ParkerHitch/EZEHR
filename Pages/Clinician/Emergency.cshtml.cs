using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>
/// Break-glass. The clinician types the patient's emergency code and a reason, and the record opens for an hour
/// with no approval. The patient turned this on ahead of time, and sees who opened it, when and why. Like the
/// share-code form, it is reached only through a launch from the clinician's own organization.
/// </summary>
public sealed class EmergencyModel(
    CallerManager callerManager,
    GrantManager grantManager,
    LaunchManager launchManager
) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid LaunchId { get; set; }

    [BindProperty]
    public string Code { get; set; } = "";

    [BindProperty]
    public string Reason { get; set; } = "";

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
        ErrorCopy = error is GrantError failed ? Copy.EmergencyAccessFailed(failed) : null;
        return Page();
    }

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
            check.Party!,
            LaunchId
        );
        if (result.Error == GrantError.LaunchNotOpen)
        {
            return RedirectToPage("/Clinician/Index");
        }
        if (!result.Succeeded)
        {
            return RedirectToPage(new { launchId = LaunchId, error = result.Error });
        }
        return RedirectToPage("/Clinician/Record", new { patientId = result.Grant!.PatientId });
    }
}
