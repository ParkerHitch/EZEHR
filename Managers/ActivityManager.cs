using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>The audit log in plain language, newest first, for the patient's Activity page.</summary>
public sealed class ActivityManager(AuditManager auditManager, IOptions<AppOptions> options)
{
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(
        options.Value.TimeZone
    );

    public async Task<IReadOnlyList<ActivityLine>> ListActivityAsync(Guid patientId)
    {
        IReadOnlyList<AuditEvent> events = await auditManager.ListForPatientAsync(patientId);
        return events
            .OrderByDescending(auditEvent => auditEvent.Id)
            .Select(auditEvent => new ActivityLine(
                auditEvent.Id,
                Copy.ActivityLine(auditEvent),
                LocalTime.Clock(auditEvent.At, _timeZone),
                !auditEvent.Granted,
                auditEvent.Action == GrantManager.OpenEmergencyAccessAction
            ))
            .ToList();
    }
}
