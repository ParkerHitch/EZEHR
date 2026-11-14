using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>
/// Waits while the patient decides. htmx polls Status every 2 s; once the request isn't pending, an approved one
/// goes to Record and anything else to AccessEnded.
/// </summary>
public sealed class WaitModel(CallerManager callerManager, GrantManager grantManager) : PageModel
{
    public Guid PatientId { get; private set; }

    public void OnGet(Guid patientId)
    {
        PatientId = patientId;
    }

    public async Task<IActionResult> OnGetStatusAsync(Guid patientId)
    {
        ClinicianCheck check = await callerManager.EvaluateClinicianAsync(User);
        if (!check.Ok)
        {
            Response.Headers["HX-Redirect"] = Url.Page("/Clinician/Index");
            return new OkResult();
        }
        Grant? grant = await grantManager.FindNewestForGranteeAsync(patientId, check.Party!);
        if (grant?.Status == GrantStatus.Pending)
        {
            return new NoContentResult();
        }
        // Only an approved request goes on to the record. A denied or withdrawn one ends here, so it isn't
        // logged as a read attempt the patient would see as "Blocked".
        Response.Headers["HX-Redirect"] =
            grant?.Status == GrantStatus.Active
                ? Url.Page("/Clinician/Record", new { patientId })
                : Url.Page("/Clinician/AccessEnded");
        return new OkResult();
    }
}
