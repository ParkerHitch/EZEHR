using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>
/// The audit log in plain language, newest first, for the patient's Activity page. Every load also recomputes the
/// log's hash chain, so the patient is told if an event was changed or removed behind the app's back.
/// </summary>
public sealed class ActivityManager(AuditManager auditManager, IOptions<AppOptions> options)
{
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(
        options.Value.TimeZone
    );

    public async Task<PatientActivity> ListActivityAsync(Guid patientId)
    {
        IReadOnlyList<AuditEvent> events = await auditManager.ListForPatientAsync(patientId);
        AuditChainCheck chain = AuditChain.Verify(events);
        string? chainStatus = chain switch
        {
            { Intact: false } => Copy.AuditLogChanged,
            { HeadHash: string headHash } => Copy.AuditLogVerified(
                chain.EventCount - chain.UnchainedCount,
                AuditChain.Fingerprint(headHash),
                chain.UnchainedCount
            ),
            _ => null,
        };
        List<ActivityLine> lines = events
            .OrderByDescending(auditEvent => auditEvent.Id)
            .Select(auditEvent => new ActivityLine(
                auditEvent.Id,
                Copy.ActivityLine(auditEvent),
                LocalTime.Clock(auditEvent.At, _timeZone),
                !auditEvent.Granted,
                auditEvent.Action == GrantManager.OpenEmergencyAccessAction
            ))
            .ToList();
        return new PatientActivity(lines, chain.Intact, chainStatus);
    }
}
