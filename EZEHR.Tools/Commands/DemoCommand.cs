using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Tools.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EZEHR.Tools.Commands;

/// <summary>
/// Backstage for the demo: empties the database before a run, and reports what Sam's record holds. Talks straight
/// to the database in AZURE_SQL_CONNECTIONSTRING as whoever is signed in with `az login`, never to the network.
/// `source scripts/azure.env` sets it for the deployed site; `scripts/local.sh` sets it for a local run. The demo's
/// network calls are `stage`.
/// </summary>
public static class DemoCommand
{
    public const string Usage = """
          demo reset [--yes]   empty the database (organizations stay), before `stage`
          demo status          Sam's patient id, what the record holds, and the latest activity
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "reset":
                return await ResetAsync(args.Contains("--yes"));
            case "status":
                return await StatusAsync();
            default:
                Console.Error.WriteLine($"usage:\n{Usage}");
                return 1;
        }
    }

    private static async Task<int> ResetAsync(bool confirmed)
    {
        Db? db = OpenDatabase();
        if (db is null)
        {
            return 1;
        }
        Console.WriteLine($"site:     {NetworkClient.BaseUrl}");
        Console.WriteLine($"database: {DescribeDatabase()}");
        if (!confirmed)
        {
            Console.Write(
                "This deletes every patient, record, grant and audit event there. Type reset: "
            );
            if (Console.ReadLine()?.Trim() != "reset")
            {
                Console.WriteLine("Nothing was deleted.");
                return 1;
            }
        }
        await db.DeleteAllDataAsync();
        Console.WriteLine("Database emptied. Next: ezehr-tools stage");
        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        Guid? samId = await FindSamAsync();
        if (samId is null)
        {
            return 1;
        }
        await using SqlConnection connection = await OpenDatabase()!.OpenAsync();
        Console.WriteLine($"Sam's patient id: {samId}");
        IEnumerable<string> links = await connection.QueryAsync<string>(
            "SELECT provider_uri FROM provider_links WHERE patient_id = @samId ORDER BY provider_uri",
            new { samId }
        );
        Console.WriteLine($"linked providers: {string.Join(", ", links)}");
        int entries = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE patient_id = @samId",
            new { samId }
        );
        Console.WriteLine($"record entries:   {entries}");
        IEnumerable<string> grants = await connection.QueryAsync<string>(
            """
            SELECT CONCAT(grantee_name, ' (', status, ')') FROM grants
            WHERE patient_id = @samId ORDER BY created_at
            """,
            new { samId }
        );
        Console.WriteLine($"grants:           {string.Join(", ", grants)}");
        IEnumerable<string> activity = await connection.QueryAsync<string>(
            """
            SELECT TOP 8 CONCAT(FORMAT(at, 'HH:mm:ss'), '  ', action, '  ',
                   COALESCE(actor_name, 'unverified'), '  ',
                   IIF(granted = 1, 'granted', CONCAT('DENIED ', reason)))
            FROM audit_events WHERE patient_id = @samId ORDER BY id DESC
            """,
            new { samId }
        );
        Console.WriteLine("latest activity (newest first):");
        foreach (string line in activity)
        {
            Console.WriteLine($"  {line}");
        }
        return 0;
    }

    /// <summary>Sam is whoever enrolled with Lakeside's invite for LUC-20417.</summary>
    public static async Task<Guid?> FindSamAsync()
    {
        Db? db = OpenDatabase();
        if (db is null)
        {
            return null;
        }
        await using SqlConnection connection = await db.OpenAsync();
        Guid? samId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT patient_id FROM provider_links
            WHERE provider_uri = 'urn:provider:lakeside' AND provider_patient_id = 'LUC-20417'
            """
        );
        if (samId is null)
        {
            Console.Error.WriteLine(
                "  Sam isn't enrolled yet: enroll on the phone with Lakeside's invite (stage step 1)."
            );
        }
        return samId;
    }

    private static Db? OpenDatabase()
    {
        if (
            string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(AppOptions.ConnectionStringKey)
            )
        )
        {
            Console.Error.WriteLine(
                $"{AppOptions.ConnectionStringKey} isn't set. For the deployed site: source scripts/azure.env"
            );
            return null;
        }
        return new Db(new ConfigurationBuilder().AddEnvironmentVariables().Build());
    }

    private static string DescribeDatabase()
    {
        SqlConnectionStringBuilder builder = new(
            Environment.GetEnvironmentVariable(AppOptions.ConnectionStringKey)
        );
        return $"{builder.InitialCatalog} on {builder.DataSource}";
    }
}
