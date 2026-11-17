using Dapper;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.Data.SqlClient;

namespace EZEHR.Tests;

/// <summary>
/// Launches for tests whose subject is something else: an already-open launch for a clinician, written straight to
/// the database, as if their organization's system had made it and they had opened it.
/// </summary>
public static class TestLaunches
{
    public const string EncounterId = "ENC-TEST-1";

    /// <summary>
    /// An open launch for the clinician from their organization. With a patient, it is from that patient's chart:
    /// the patient is linked to the organization first if they aren't. Without one, it is from the chart of someone
    /// EZEHR doesn't know, which is all emergency access needs.
    /// </summary>
    public static async Task<Guid> OpenAsync(
        Db db,
        TimeProvider clock,
        Party clinician,
        Guid? patientId
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        string providerPatientId = "MRN-NOT-ON-EZEHR";
        if (patientId is Guid linkedPatientId)
        {
            providerPatientId = await connection.QuerySingleAsync<string>(
                """
                DECLARE @linkedAs NVARCHAR(100) = (
                    SELECT TOP 1 provider_patient_id FROM provider_links
                    WHERE patient_id = @linkedPatientId AND provider_uri = @providerUri);
                IF @linkedAs IS NULL
                BEGIN
                    SET @linkedAs = @newProviderPatientId;
                    INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id)
                    VALUES (@linkedPatientId, @providerUri, @linkedAs);
                END
                SELECT @linkedAs;
                """,
                new
                {
                    linkedPatientId,
                    providerUri = clinician.OrgUri,
                    newProviderPatientId = $"MRN-{linkedPatientId:N}"[..20],
                }
            );
        }
        Guid launchId = Guid.NewGuid();
        DateTimeOffset now = clock.GetUtcNow();
        await connection.ExecuteAsync(
            """
            INSERT INTO clinician_launches(id, code, provider_uri, clinician_uri, provider_patient_id,
                                           encounter_id, created_at, open_by, opened_at, ends_at)
            VALUES (@launchId, @code, @providerUri, @clinicianUri, @providerPatientId, @encounterId, @now,
                    @now, @now, @endsAt)
            """,
            new
            {
                launchId,
                code = GrantManager.GenerateCode(),
                providerUri = clinician.OrgUri,
                clinicianUri = clinician.SubjectUri,
                providerPatientId,
                encounterId = EncounterId,
                now,
                endsAt = now + LaunchManager.LaunchLifetime,
            }
        );
        return launchId;
    }
}
