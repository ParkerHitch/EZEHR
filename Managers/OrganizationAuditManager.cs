using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>
/// An organization's view of the audit log: every attempt by someone it vouches for (its clinicians, granted or
/// refused, and its own system), and nothing by anyone else, even for the same patient.
/// </summary>
public sealed class OrganizationAuditManager(Db db)
{
    public async Task<ReadOrganizationAuditResult> ReadOrganizationAuditAsync(
        Caller caller,
        OrganizationAuditFilter filter
    )
    {
        if (RefuseCaller(caller) is DenyReason refused)
        {
            return new ReadOrganizationAuditResult(false, refused, [], null);
        }
        string organizationUri = ((VerifiedCaller)caller).Party.OrgUri;

        await using SqlConnection connection = await db.OpenAsync();
        // One row more than the page holds tells us whether another page follows.
        List<OrganizationAuditRow> rows = (
            await connection.QueryAsync<OrganizationAuditRow>(
                """
                SELECT TOP (@take) a.id AS Id, a.at AS At, a.patient_id AS PatientId,
                       link.provider_patient_id AS ProviderPatientId, a.actor_kind AS ActorKind,
                       a.actor_uri AS ActorUri, a.actor_name AS ActorName, a.action AS Action,
                       a.claimed_purpose AS ClaimedPurpose, a.granted AS Granted, a.reason AS Reason
                FROM audit_events a
                OUTER APPLY (SELECT TOP 1 l.provider_patient_id FROM provider_links l
                             WHERE l.patient_id = a.patient_id AND l.provider_uri = @organizationUri
                             ORDER BY l.provider_patient_id) link
                WHERE a.actor_org_uri = @organizationUri AND a.id > @afterId
                  AND (@clinicianUri IS NULL OR a.actor_uri = @clinicianUri)
                  AND (@patientId IS NULL OR a.patient_id = @patientId)
                  AND (@providerPatientId IS NULL OR EXISTS (
                        SELECT 1 FROM provider_links p
                        WHERE p.patient_id = a.patient_id AND p.provider_uri = @organizationUri
                          AND p.provider_patient_id = @providerPatientId))
                  AND (@since IS NULL OR a.at >= @since)
                  AND (@until IS NULL OR a.at < @until)
                ORDER BY a.id
                """,
                new
                {
                    take = filter.Limit + 1,
                    organizationUri,
                    afterId = filter.AfterId,
                    clinicianUri = filter.ClinicianUri,
                    patientId = filter.PatientId,
                    providerPatientId = filter.ProviderPatientId,
                    since = filter.Since,
                    until = filter.Until,
                }
            )
        ).ToList();

        List<OrganizationAuditEvent> events = rows.Take(filter.Limit)
            .Select(row => row.ToOrganizationAuditEvent())
            .ToList();
        long? nextAfterId = rows.Count > filter.Limit ? events[^1].Id : null;
        return new ReadOrganizationAuditResult(true, null, events, nextAfterId);
    }

    /// <summary>Only a verified organization (provider or app) audits, and only itself.</summary>
    private static DenyReason? RefuseCaller(Caller caller)
    {
        if (caller is UnverifiedCaller unverified)
        {
            return unverified.Reason;
        }
        if (((VerifiedCaller)caller).Party.Kind == PartyKind.Clinician)
        {
            return DenyReason.WrongPartyKind;
        }
        return null;
    }

    private sealed record OrganizationAuditRow(
        long Id,
        DateTimeOffset At,
        Guid PatientId,
        string? ProviderPatientId,
        string ActorKind,
        string ActorUri,
        string ActorName,
        string Action,
        string? ClaimedPurpose,
        bool Granted,
        string? Reason
    )
    {
        public OrganizationAuditEvent ToOrganizationAuditEvent()
        {
            DenyReason? reason = Reason is null ? null : Enum.Parse<DenyReason>(Reason);
            return new OrganizationAuditEvent(
                Id,
                At,
                PatientId,
                ProviderPatientId,
                Enum.Parse<PartyKind>(ActorKind),
                ActorUri,
                ActorName,
                Action,
                ClaimedPurpose,
                Granted,
                reason
            );
        }
    }
}
