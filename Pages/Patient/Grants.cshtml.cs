using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>Who can see the record now, with a Revoke button for each, and who could before.</summary>
public sealed class GrantsModel(CallerManager callerManager, GrantManager grantManager) : PageModel
{
    public IReadOnlyList<AccessLine> Lines { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Lines = await grantManager.ListAccessAsync(callerManager.PatientIdFrom(User)!.Value);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid grantId)
    {
        await grantManager.RevokeAsync(callerManager.PatientIdFrom(User)!.Value, grantId);
        return RedirectToPage();
    }
}
