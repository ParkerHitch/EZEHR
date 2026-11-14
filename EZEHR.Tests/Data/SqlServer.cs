using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Testcontainers.MsSql;

namespace EZEHR.Tests.Data;

/// <summary>
/// One SQL Server for the whole test run: a throwaway container (needs Docker, or Podman with DOCKER_HOST set),
/// or the server in EZEHR_TEST_SQL when that is set. Every test gets its own database (xUnit runs
/// InitializeAsync once per test), so test classes still run in parallel and never share state.
/// </summary>
public static class SqlServer
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private static readonly Lazy<Task<string>> ServerConnectionString = new(StartAsync);

    public static async Task<TestDatabase> CreateDatabaseAsync()
    {
        string server = await ServerConnectionString.Value;
        string name = $"ezehr_test_{Guid.NewGuid():N}";
        await using (SqlConnection master = new(server))
        {
            await master.OpenAsync();
            await master.ExecuteAsync(
                """
                DECLARE @sql NVARCHAR(200) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
                """,
                new { name }
            );
        }

        SqlConnectionStringBuilder connectionString = new(server) { InitialCatalog = name };
        TestDatabase database = new(name, server, connectionString.ConnectionString);
        await database.Db.EnsureSchemaAsync();
        return database;
    }

    private static async Task<string> StartAsync()
    {
        string? external = Environment.GetEnvironmentVariable("EZEHR_TEST_SQL");
        if (!string.IsNullOrEmpty(external))
        {
            return external;
        }
        // Testcontainers' reaper removes the container when the test run ends.
        MsSqlContainer container = new MsSqlBuilder(Image).Build();
        await container.StartAsync();
        return container.GetConnectionString();
    }
}

/// <summary>A fresh, schema-ready database. Dispose drops it.</summary>
public sealed class TestDatabase(
    string name,
    string serverConnectionString,
    string connectionString
) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public Db Db { get; } =
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [AppOptions.ConnectionStringKey] = connectionString,
                    }
                )
                .Build()
        );

    public async ValueTask DisposeAsync()
    {
        await using SqlConnection master = new(serverConnectionString);
        await master.OpenAsync();
        await master.ExecuteAsync(
            """
            DECLARE @sql NVARCHAR(400) =
                N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; '
                + N'DROP DATABASE ' + QUOTENAME(@name);
            EXEC (@sql);
            """,
            new { name }
        );
    }
}
