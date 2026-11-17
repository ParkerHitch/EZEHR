using Dapper;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Patient;

/// <summary>Every enrolled patient, with the provider charts linked to them and how much their record holds.</summary>
public sealed class PatientListCommand : AsyncCommand
{
    private sealed record PatientRow(
        Guid Id,
        string DisplayName,
        DateTimeOffset CreatedAt,
        string? Links,
        int EntryCount
    );

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CancellationToken cancellationToken
    )
    {
        await using SqlConnection connection = await ToolDatabase.Open().OpenAsync();
        IEnumerable<PatientRow> patients = await connection.QueryAsync<PatientRow>(
            """
            SELECT p.id AS Id, p.display_name AS DisplayName, p.created_at AS CreatedAt,
                   (SELECT STRING_AGG(CONCAT(l.provider_uri, ' ', l.provider_patient_id), ', ')
                    FROM provider_links l WHERE l.patient_id = p.id) AS Links,
                   (SELECT COUNT(*) FROM entries e WHERE e.patient_id = p.id) AS EntryCount
            FROM patients p
            ORDER BY p.created_at
            """
        );
        Table table = new Table()
            .AddColumn("patient id")
            .AddColumn("name")
            .AddColumn("enrolled")
            .AddColumn("linked charts")
            .AddColumn("entries");
        foreach (PatientRow patient in patients)
        {
            table.AddRow(
                new Text(patient.Id.ToString()),
                new Text(patient.DisplayName),
                new Text($"{patient.CreatedAt:yyyy-MM-dd HH:mm}"),
                new Text(patient.Links ?? ""),
                new Text(patient.EntryCount.ToString())
            );
        }
        AnsiConsole.Console.Write(table);
        return 0;
    }
}
