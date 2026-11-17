using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Database;

/// <summary>Any SQL against the configured database, local or deployed, with the rows as a table.</summary>
public sealed class DbQueryCommand : AsyncCommand<DbQueryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<sql>")]
        [Description("The SQL to run, quoted. It runs as-is, so a DELETE deletes")]
        public string Sql { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        await using SqlConnection connection = await ToolDatabase.Open().OpenAsync();
        await using SqlCommand command = new(settings.Sql, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (reader.FieldCount == 0)
        {
            AnsiConsole.Console.WriteLine($"{reader.RecordsAffected} rows affected");
            return 0;
        }
        Table table = new();
        for (int column = 0; column < reader.FieldCount; column++)
        {
            table.AddColumn(new TableColumn(new Text(reader.GetName(column))));
        }
        int rowCount = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            Text[] cells = new Text[reader.FieldCount];
            for (int column = 0; column < reader.FieldCount; column++)
            {
                cells[column] = new Text(
                    reader.IsDBNull(column)
                        ? "NULL"
                        : Convert.ToString(reader.GetValue(column)) ?? ""
                );
            }
            table.AddRow(cells);
            rowCount++;
        }
        AnsiConsole.Console.Write(table);
        AnsiConsole.Console.WriteLine($"{rowCount} rows");
        return 0;
    }
}
