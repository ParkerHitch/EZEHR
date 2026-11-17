using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>
/// Who can see the record now, with a Revoke button for each, and who could before. Below that, the providers that
/// can add records (provider links), with a Disconnect button for each. Apps: every app on the network, each on or off.
/// </summary>
public sealed class GrantsModel(
    CallerManager callerManager,
    GrantManager grantManager,
    InviteManager inviteManager
) : PageModel
{
    public IReadOnlyList<AccessLine> Lines { get; private set; } = [];

    public IReadOnlyList<ConnectedProviderLine> ConnectedProviders { get; private set; } = [];

    public IReadOnlyList<AppLine> Apps { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        AccessOverview overview = await grantManager.ListAccessAsync(
            callerManager.PatientIdFrom(User)!.Value
        );
        Lines = overview.Lines;
        ConnectedProviders = overview.ConnectedProviders;
        Apps = overview.Apps;
        return Page();
    }

    public async Task<IActionResult> OnPostTurnOnAppAsync(string appUri)
    {
        await grantManager.TurnOnAppAsync(callerManager.PatientIdFrom(User)!.Value, appUri);
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
