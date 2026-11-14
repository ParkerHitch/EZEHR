using EZEHR.Managers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>The signed-in patient's home. Task 11 adds the timeline, sharing and activity.</summary>
public sealed class HomeModel(CallerManager callerManager, EnrollmentManager enrollmentManager)
    : PageModel
{
    public string PatientName { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        Guid patientId = callerManager.PatientIdFrom(User)!.Value;
        PatientName = await enrollmentManager.FindPatientNameAsync(patientId) ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(CallerManager.PatientScheme);
        return RedirectToPage("/Patient/SignIn");
    }
}
