using Dapper;
using EZEHR.Tests.Data;
using Microsoft.Data.SqlClient;

namespace EZEHR.Tests.Tools;

/// <summary>The backstage commands against a real database (needs Docker, or EZEHR_TEST_SQL).</summary>
[Collection(ToolConsoleCollection.Name)]
public sealed class PatientCommandTests : IAsyncLifetime
{
    private readonly Guid _patientId = Guid.NewGuid();

    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO patients(id, display_name, wrapped_dek, created_at)
            VALUES (@patientId, 'Test Patient', 0x00, SYSDATETIMEOFFSET());
            INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id, linked_at)
            VALUES (@patientId, 'urn:provider:lakeside', 'LUC-20417', SYSDATETIMEOFFSET());
            """,
            new { patientId = _patientId }
        );
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task FindByChart()
    {
        ToolRun found = await RunAsync("patient", "find", "lakeside:LUC-20417");
        ToolRun missing = await RunAsync("patient", "find", "lakeside:NOPE");

        Assert.Equal(0, found.ExitCode);
        Assert.Equal(_patientId.ToString(), found.Output.Trim());
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("provider invite lakeside NOPE", missing.Output);
    }

    [Fact]
    public async Task ListAndStatus()
    {
        ToolRun list = await RunAsync("patient", "list");
        ToolRun status = await RunAsync("patient", "status", _patientId.ToString());

        Assert.Equal(0, list.ExitCode);
        Assert.Contains(_patientId.ToString(), list.Output);
        Assert.Contains("urn:provider:lakeside", list.Output);
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("record entries:", status.Output);
        Assert.Contains("audit log:", status.Output);
    }

    [Fact]
    public async Task DbQueryPrintsRows()
    {
        ToolRun run = await RunAsync("db", "query", "SELECT 1 AS one");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("one", run.Output);
    }

    [Fact]
    public async Task BadSqlIsOneLine()
    {
        ToolRun run = await RunAsync("db", "query", "SELECT nonsense FROM nowhere");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("nowhere", run.Output);
        Assert.DoesNotContain("\n  at ", run.Output);
    }

    [Fact]
    public async Task UnreachableDatabaseIsOneLine()
    {
        ToolRun run = await ToolRunner.RunAsync(
            new Dictionary<string, string?>
            {
                ["AZURE_SQL_CONNECTIONSTRING"] =
                    "Server=127.0.0.1,1;Database=nowhere;Connect Timeout=2;TrustServerCertificate=True",
            },
            "patient",
            "list"
        );

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("nowhere on 127.0.0.1,1", run.Output);
        Assert.DoesNotContain("\n  at ", run.Output);
    }

    private Task<ToolRun> RunAsync(params string[] args)
    {
        return ToolRunner.RunAsync(
            new Dictionary<string, string?>
            {
                ["AZURE_SQL_CONNECTIONSTRING"] = _database.ConnectionString,
            },
            args
        );
    }
}
