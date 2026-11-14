using EZEHR.Managers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>
/// Usernameless passkey sign-in; passkey.js runs the ceremony against PatientPasskeyController. Every patient page
/// signs out here too (the Sign out button in _PatientNav).
/// </summary>
public sealed class SignInModel : PageModel
{
    public void OnGet() { }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(CallerManager.PatientScheme);
        return RedirectToPage("/Patient/SignIn");
    }
}
