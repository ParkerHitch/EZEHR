using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>
/// Who can see the record now, with a Revoke button for each, and who could before. Below that, the clinics that
/// can add records (provider links), with a Disconnect button for each. Kidney Check can be turned on here too.
/// </summary>
public sealed class GrantsModel(
    CallerManager callerManager,
    GrantManager grantManager,
    InviteManager inviteManager
) : PageModel
{
    public IReadOnlyList<AccessLine> Lines { get; private set; } = [];

    public IReadOnlyList<ConnectedProviderLine> ConnectedProviders { get; private set; } = [];

    public bool KidneyCheckOn { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        AccessOverview overview = await grantManager.ListAccessAsync(
            callerManager.PatientIdFrom(User)!.Value
        );
        Lines = overview.Lines;
        ConnectedProviders = overview.ConnectedProviders;
        KidneyCheckOn = overview.KidneyCheckOn;
        return Page();
    }

    public async Task<IActionResult> OnPostTurnOnKidneyCheckAsync()
    {
        await grantManager.TurnOnKidneyCheckAsync(callerManager.PatientIdFrom(User)!.Value);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid grantId)
    {
        await grantManager.RevokeAsync(callerManager.PatientIdFrom(User)!.Value, grantId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisconnectAsync(string providerUri)
    {
        await inviteManager.DisconnectProviderAsync(
            callerManager.PatientIdFrom(User)!.Value,
            providerUri
        );
        return RedirectToPage();
    }
}
