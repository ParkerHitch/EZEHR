using System.Security.Claims;
using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>
/// Opened from a provider's invite link. A new patient creates a passkey here (passkey.js); a signed-in patient
/// adds that provider to their record instead.
/// </summary>
public sealed class EnrollModel(CallerManager callerManager, EnrollmentManager enrollmentManager)
    : PageModel
{
    [BindProperty(SupportsGet = true, Name = "invite")]
    public string InviteCode { get; set; } = "";

    public InviteCheck Invite { get; private set; } =
        new(false, null, EnrollmentError.InviteUnknown, []);

    public bool SignedIn { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Invite = await enrollmentManager.CheckInviteAsync(InviteCode);
        SignedIn = callerManager.PatientIdFrom(await SignedInPatientAsync()) is not null;
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync()
    {
        Guid? patientId = callerManager.PatientIdFrom(await SignedInPatientAsync());
        if (patientId is null)
        {
            return RedirectToPage("/Patient/SignIn");
        }
        EnrollmentResult result = await enrollmentManager.LinkInviteAsync(
            patientId.Value,
            InviteCode
        );
        if (!result.Succeeded)
        {
            SignedIn = true;
            ErrorMessage = Copy.EnrollmentFailed(result.Error!.Value);
            return Page();
        }
        return RedirectToPage("/Patient/Home");
    }

    /// <summary>Someone else's account is signed in on this phone: sign it out and show enrollment.</summary>
    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(CallerManager.PatientScheme);
        return RedirectToPage("/Patient/Enroll", new { invite = InviteCode });
    }

    // This page is open to everyone, so the patient cookie is read explicitly.
    private async Task<ClaimsPrincipal?> SignedInPatientAsync()
    {
        AuthenticateResult authentication = await HttpContext.AuthenticateAsync(
            CallerManager.PatientScheme
        );
        return authentication.Principal;
    }
}
