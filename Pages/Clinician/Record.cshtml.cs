using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EZEHR.Pages.Clinician;

/// <summary>
/// The patient's record, read-only. Every load goes through the access check, so a grant that ended while the
/// page was open shows AccessEnded on the next load.
/// </summary>
public sealed class RecordModel(
    CallerManager callerManager,
    RecordManager recordManager,
    IOptions<AppOptions> options
) : PageModel
{
    public string Header { get; private set; } = "";

    public Timeline Timeline { get; private set; } = new([], []);

    public async Task<IActionResult> OnGetAsync(Guid patientId)
    {
        Caller caller = callerManager.FromClinician(User);
        ReadFullRecordResult result = await recordManager.ReadFullRecordAsync(
            patientId,
            caller,
            null
        );
        if (!result.Allowed)
        {
            return result.Reason switch
            {
                DenyReason.GrantPending => RedirectToPage("/Clinician/Wait", new { patientId }),
                DenyReason.NotSignedIn => RedirectToPage("/Clinician/Index"),
                DenyReason.ShiftEnded => RedirectToPage(
                    "/Clinician/AccessEnded",
                    new { reason = DenyReason.ShiftEnded }
                ),
                _ => RedirectToPage("/Clinician/AccessEnded"),
            };
        }
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
}
