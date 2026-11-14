using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EZEHR.Pages.Clinician;

/// <summary>Clinician sign-in: who is signed in and until when, why an account isn't allowed, or a sign-in link.</summary>
public sealed class IndexModel(
    CallerManager callerManager,
    IAuthenticationSchemeProvider authenticationSchemes,
    IOptions<AppOptions> options
) : PageModel
{
    public string? SignedInBanner { get; private set; }

    public string? ErrorCopy { get; private set; }

    public bool SignInAvailable { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        SignInAvailable =
            await authenticationSchemes.GetSchemeAsync(CallerManager.ClinicianOidcScheme)
                is not null;
        // This page is open to everyone, so read the clinician cookie ourselves.
        AuthenticateResult authentication = await HttpContext.AuthenticateAsync(
            CallerManager.ClinicianScheme
        );
        if (!authentication.Succeeded)
        {
            return Page();
        }
        ClinicianCheck check = callerManager.EvaluateClinician(authentication.Principal);
        if (!check.Ok)
        {
            ErrorCopy = check.ErrorCopy;
            return Page();
        }
        if (check.SessionStartedAt is DateTimeOffset startedAt)
        {
            string until = LocalTime.Clock(
                startedAt + AccessManager.ClinicianShift,
                TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone)
            );
            SignedInBanner = Copy.ClinicianSignedIn(
                check.Party!.DisplayName,
                check.Party.OrgName,
                until
            );
        }
        return Page();
    }

    public async Task<IActionResult> OnGetSignInAsync()
    {
        if (await authenticationSchemes.GetSchemeAsync(CallerManager.ClinicianOidcScheme) is null)
        {
            return RedirectToPage("/Clinician/Index");
        }
        AuthenticationProperties properties = new() { RedirectUri = Url.Page("/Clinician/Index") };
        return Challenge(properties, CallerManager.ClinicianOidcScheme);
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync(CallerManager.ClinicianScheme);
        return RedirectToPage("/Clinician/Index");
    }
}
