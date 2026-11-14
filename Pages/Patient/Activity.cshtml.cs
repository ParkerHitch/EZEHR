using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Patient;

/// <summary>Every access attempt on the patient's record, granted or blocked, refreshed every 2 s.</summary>
public sealed class ActivityModel(CallerManager callerManager, ActivityManager activityManager)
    : PageModel
{
    public IReadOnlyList<ActivityLine> Lines { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Lines = await activityManager.ListActivityAsync(callerManager.PatientIdFrom(User)!.Value);
        return Page();
    }

    public async Task<IActionResult> OnGetListAsync()
    {
        IReadOnlyList<ActivityLine> lines = await activityManager.ListActivityAsync(
            callerManager.PatientIdFrom(User)!.Value
        );
        return Partial("_ActivityPartial", lines);
    }
}
