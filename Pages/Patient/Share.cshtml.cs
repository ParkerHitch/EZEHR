using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>
/// The patient makes a one-time code for a clinician, watches for the request it brings (htmx, every 2 s),
/// and approves or denies the named clinician.
/// </summary>
public sealed class ShareModel(CallerManager callerManager, GrantManager grantManager) : PageModel
{
    public ShareStatus? Status { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? offerId)
    {
        if (offerId is Guid id)
        {
            Status = await grantManager.ReadShareStatusAsync(PatientId(), id);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(ShareDuration duration)
    {
        ShareOffer offer = await grantManager.CreateShareOfferAsync(PatientId(), duration);
        return RedirectToPage(new { offerId = offer.Id });
    }

    /// <summary>The polled status block: waiting, the approve prompt, or the outcome.</summary>
    public async Task<IActionResult> OnGetStatusAsync(Guid offerId)
    {
        ShareStatus? status = await grantManager.ReadShareStatusAsync(PatientId(), offerId);
        if (status is null)
        {
            return NotFound();
        }
        return Partial("_ShareStatus", status);
    }

    public async Task<IActionResult> OnPostDecideAsync(Guid offerId, Guid grantId, bool approve)
    {
        await grantManager.ApproveOrDenyAsync(PatientId(), grantId, approve);
        return RedirectToPage(new { offerId });
    }

    private Guid PatientId()
    {
        return callerManager.PatientIdFrom(User)!.Value;
    }
}
