using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EZEHR.Pages.Clinician;

/// <summary>
/// The patient's record, read-only. Every load goes through the access check. While the page is open, htmx polls
/// Status every 2 s, so a revoke, an expiry or the end of the shift locks the page without a reload.
/// </summary>
public sealed class RecordModel(
    CallerManager callerManager,
    RecordManager recordManager,
    AccessManager accessManager,
    IOptions<AppOptions> options
) : PageModel
{
    public Guid PatientId { get; private set; }

    public string Header { get; private set; } = "";

    public Timeline Timeline { get; private set; } = new([], []);

    public async Task<IActionResult> OnGetAsync(Guid patientId)
    {
        Caller caller = await callerManager.FromClinicianAsync(User);
        ReadFullRecordResult result = await recordManager.ReadFullRecordAsync(
            patientId,
            caller,
            null
        );
        if (!result.Allowed)
        {
            return Redirect(DeniedPath(patientId, result.Reason));
        }
        PatientId = patientId;
        string until = LocalTime.Clock(
            result.Grant!.ExpiresAt!.Value,
            TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone)
        );
        Header = Copy.ClinicianRecordHeader(result.PatientName ?? "", until);
        Timeline = TimelineBuilder.Build(result.Entries) with
        {
            UnverifiedCount = result.UnverifiedCount,
        };
        return Page();
    }

    /// <summary>
    /// The poll. It reads no record data, so it isn't audited: Sam's activity shows each page load, not every
    /// 2-second check. Once access has ended it sends the browser straight to where a reload would have gone.
    /// </summary>
    public async Task<IActionResult> OnGetStatusAsync(Guid patientId)
    {
        Caller caller = await callerManager.FromClinicianAsync(User);
        AccessDecision decision = await accessManager.CheckAccessWithoutAuditAsync(
            patientId,
            AccessAction.ReadFullRecord,
            caller
        );
        if (decision.Allowed)
        {
            return new NoContentResult();
        }
        Response.Headers["HX-Redirect"] = DeniedPath(patientId, decision.Reason);
        return new OkResult();
    }

    private string DeniedPath(Guid patientId, DenyReason? reason)
    {
        return reason switch
        {
            DenyReason.GrantPending => Url.Page("/Clinician/Wait", new { patientId })!,
            DenyReason.NotSignedIn => Url.Page("/Clinician/Index")!,
            DenyReason.ShiftEnded => Url.Page(
                "/Clinician/AccessEnded",
                new { reason = DenyReason.ShiftEnded }
            )!,
            _ => Url.Page("/Clinician/AccessEnded")!,
        };
    }
}
