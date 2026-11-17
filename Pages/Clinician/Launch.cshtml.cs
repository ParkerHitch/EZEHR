using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>
/// Where an organization's system sends its clinician: the launch link from create-clinician-launch. Signs the
/// clinician in if they aren't, opens the launch for them, and goes on to the share-code form.
/// </summary>
public sealed class LaunchModel(
    CallerManager callerManager,
    LaunchManager launchManager,
    IAuthenticationSchemeProvider authenticationSchemes
) : PageModel
{
    public string ErrorCopy { get; private set; } = Copy.LaunchUnusable;

    public async Task<IActionResult> OnGetAsync(string? launch)
    {
        // This page is open to everyone, so read the clinician cookie ourselves.
        AuthenticateResult authentication = await HttpContext.AuthenticateAsync(
            CallerManager.ClinicianScheme
        );
        if (!authentication.Succeeded)
        {
            if (
                await authenticationSchemes.GetSchemeAsync(CallerManager.ClinicianOidcScheme)
                is null
            )
            {
                return RedirectToPage("/Clinician/Index");
            }
            // Sign in, then come back to this same link.
            AuthenticationProperties properties = new()
            {
                RedirectUri = Url.Page("/Clinician/Launch", new { launch }),
            };
            return Challenge(properties, CallerManager.ClinicianOidcScheme);
        }
        ClinicianCheck check = await callerManager.EvaluateClinicianAsync(authentication.Principal);
        if (!check.Ok)
        {
            return RedirectToPage("/Clinician/Index");
        }
        OpenClinicianLaunchResult result = await launchManager.OpenClinicianLaunchAsync(
            launch,
            check.Party!
        );
        if (!result.Succeeded)
        {
            ErrorCopy = Copy.LaunchFailed(result.Error!.Value);
            return Page();
        }
        return RedirectToPage("/Clinician/Redeem", new { launchId = result.Launch!.Id });
    }
}
