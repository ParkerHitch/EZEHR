using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>Every access attempt on the patient's record, granted or blocked, refreshed every 2 s.</summary>
public sealed class ActivityModel(CallerManager callerManager, ActivityManager activityManager)
    : PageModel
{
    public PatientActivity Activity { get; private set; } = new([], true, null);

    public async Task<IActionResult> OnGetAsync()
    {
        Activity = await activityManager.ListActivityAsync(
            callerManager.PatientIdFrom(User)!.Value
        );
        return Page();
    }

    public async Task<IActionResult> OnGetListAsync()
    {
        PatientActivity activity = await activityManager.ListActivityAsync(
            callerManager.PatientIdFrom(User)!.Value
        );
        return Partial("_ActivityPartial", activity);
    }
}
