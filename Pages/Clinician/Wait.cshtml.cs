using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EZEHR.Pages.Clinician;

/// <summary>Waits while the patient decides. htmx polls Status every 2 s; once the request isn't pending, Record decides.</summary>
public sealed class WaitModel(CallerManager callerManager, GrantManager grantManager) : PageModel
{
    public Guid PatientId { get; private set; }

    public void OnGet(Guid patientId)
    {
        PatientId = patientId;
    }

    public async Task<IActionResult> OnGetStatusAsync(Guid patientId)
    {
        ClinicianCheck check = callerManager.EvaluateClinician(User);
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
        Response.Headers["HX-Redirect"] = Url.Page("/Clinician/Record", new { patientId });
        return new OkResult();
    }
}
