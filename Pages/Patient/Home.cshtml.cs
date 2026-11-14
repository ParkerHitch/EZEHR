using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>The patient's combined record: app notes on top, then every provider's entries by visit date.</summary>
public sealed class HomeModel(CallerManager callerManager, RecordManager recordManager) : PageModel
{
    public PatientHome Home { get; private set; } = new("", new Timeline([], []));

    public async Task<IActionResult> OnGetAsync()
    {
        Guid patientId = callerManager.PatientIdFrom(User)!.Value;
        Home = await recordManager.ReadPatientHomeAsync(patientId);
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(CallerManager.PatientScheme);
        return RedirectToPage("/Patient/SignIn");
    }
}
