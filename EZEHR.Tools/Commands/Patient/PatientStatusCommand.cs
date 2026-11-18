using Dapper;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Services;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Patient;

/// <summary>What a patient's record holds, who has access, and whether the audit log still verifies.</summary>
public sealed class PatientStatusCommand : AsyncCommand<PatientSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        PatientSettings settings,
        CancellationToken cancellationToken
    )
    {
        Guid patientId = await settings.Patient.ResolveAsync();
        await PatientStatus.WriteAsync(patientId);
        return 0;
    }
}

public static class PatientStatus
{
    public static async Task WriteAsync(Guid patientId)
    {
        IAnsiConsole console = AnsiConsole.Console;
        Db database = ToolDatabase.Open();
        await using SqlConnection connection = await database.OpenAsync();
        console.WriteLine($"patient id:       {patientId}");
        IEnumerable<string> links = await connection.QueryAsync<string>(
            """
            SELECT CONCAT(provider_uri, ' ', provider_patient_id) FROM provider_links
            WHERE patient_id = @patientId ORDER BY provider_uri
            """,
            new { patientId }
        );
        console.WriteLine($"linked providers: {string.Join(", ", links)}");
        int entries = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE patient_id = @patientId",
            new { patientId }
        );
        console.WriteLine($"record entries:   {entries}");
        IEnumerable<string> grants = await connection.QueryAsync<string>(
            """
            SELECT CONCAT(grantee_name, ' (', status,
                          IIF(emergency_reason IS NULL, '', CONCAT(', EMERGENCY: ', emergency_reason)), ')')
            FROM grants
            WHERE patient_id = @patientId ORDER BY created_at
            """,
            new { patientId }
        );
        console.WriteLine($"grants:           {string.Join(", ", grants)}");
        string? emergencyCode = await connection.ExecuteScalarAsync<string?>(
            "SELECT emergency_code FROM patients WHERE id = @patientId",
            new { patientId }
        );
        console.WriteLine(
            $"emergency code:   {emergencyCode ?? "off (the patient turns it on from Access → Emergency access)"}"
        );
        AuditChainCheck chain = await new AuditManager(
            database,
            TimeProvider.System
        ).VerifyChainAsync(patientId);
        console.WriteLine(
            chain.Intact
                ? $"audit log:        intact, {chain.EventCount} events, head {chain.HeadHash}"
                : $"audit log:        BROKEN at event {chain.FirstBrokenId} of {chain.EventCount}"
        );
        IEnumerable<string> activity = await connection.QueryAsync<string>(
            """
            SELECT TOP 8 CONCAT(FORMAT(at, 'HH:mm:ss'), '  ', action, '  ',
                   COALESCE(actor_name, 'unverified'), '  ',
                   IIF(granted = 1, 'granted', CONCAT('DENIED ', reason)))
            FROM audit_events WHERE patient_id = @patientId ORDER BY id DESC
            """,
            new { patientId }
        );
        console.WriteLine("latest activity (newest first):");
        foreach (string line in activity)
        {
            console.WriteLine($"  {line}");
        }
    }
}
