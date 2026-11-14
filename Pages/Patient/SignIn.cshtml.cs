using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>Usernameless passkey sign-in; passkey.js runs the ceremony against PatientPasskeyController.</summary>
public sealed class SignInModel : PageModel
{
    public void OnGet() { }
}
