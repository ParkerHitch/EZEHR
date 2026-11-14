using EZEHR.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>Where the record sends a clinician whose grant or shift has ended.</summary>
public sealed class AccessEndedModel : PageModel
{
    public bool ShiftEnded { get; private set; }

    public void OnGet(DenyReason? reason)
    {
        ShiftEnded = reason == DenyReason.ShiftEnded;
    }
}
