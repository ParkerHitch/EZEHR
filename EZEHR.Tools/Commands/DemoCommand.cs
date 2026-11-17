using Dapper;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
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
          demo reset [--yes]   empty the database and take Northwind and Medication Check off the network,
                               before `stage`
          demo tamper          edit one of Sam's audit events in the database, as an insider would; run it
                               again to put it back. Sam's Activity shows the log as broken, then verified
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "reset":
                return await ResetAsync(args.Contains("--yes"));
            case "tamper":
                return await TamperAsync();
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
        Console.WriteLine($"database: {ToolDatabase.Describe()}");
        if (!confirmed)
        {
            Console.Write(
                "This deletes every patient, record, grant and audit event there, and the Northwind and Medication Check organizations. Type reset: "
            );
            if (Console.ReadLine()?.Trim() != "reset")
            {
                Console.WriteLine("Nothing was deleted.");
                return 1;
            }
        }
        await db.DeleteAllDataAsync();
        // Stage has Northwind and Medication Check join the network, so both have to start off it. Only their rows
        // go: the other organizations, and any sysadmin edits to them (Riverside's tenant), stay.
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM organizations WHERE uri IN (@northwindUri, @appUri)",
            new
            {
                northwindUri = OrgDirectory.ProviderUri(StageCommand.JoiningProviderSlug),
                appUri = OrgDirectory.AppUri(StageCommand.JoiningAppSlug),
            }
        );
        Console.WriteLine(
            "Database emptied, and Northwind and Medication Check are off the network until stage adds them. Next: ezehr-tools stage"
        );
        return 0;
    }

    private static async Task<Guid?> FindSamAsync()
    {
        Guid? samId = await ToolDatabase.FindSamAsync();
        if (samId is null)
        {
            Console.Error.WriteLine(
                "  Sam isn't enrolled yet: enroll on the phone with one of the invite links."
            );
        }
        return samId;
    }

    /// <summary>
    /// Flips granted on the broker's first refused request to Sam, straight in the database, so the log now says the
    /// broker got in. The fingerprints no longer match, and Sam's Activity shows the warning on its next load.
    /// Flipping it again restores the event exactly, and the same fingerprint comes back.
    /// </summary>
    private static async Task<int> TamperAsync()
    {
        Guid? samId = await FindSamAsync();
        if (samId is null)
        {
            return 1;
        }
        Db database = OpenDatabase()!;
        await using SqlConnection connection = await database.OpenAsync();
        // The broker's no-certificate attempt: the reason stays put when granted flips, so this finds it both ways.
        long? eventId = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT TOP 1 id FROM audit_events
            WHERE patient_id = @samId AND reason = @reason ORDER BY id
            """,
            new { samId, reason = nameof(DenyReason.NoCertificate) }
        );
        if (eventId is null)
        {
            Console.Error.WriteLine(
                "  No refused broker request to edit yet: run stage's broker step first."
            );
            return 1;
        }
        bool nowGranted = await connection.ExecuteScalarAsync<bool>(
            """
            UPDATE audit_events SET granted = 1 - granted OUTPUT inserted.granted
            WHERE id = @eventId
            """,
            new { eventId }
        );
        Console.WriteLine(
            nowGranted
                ? $"Audit event {eventId} now says the broker's request was granted. Reload Sam's Activity: the log shows as changed."
                : $"Audit event {eventId} is back as it was (refused). Reload Sam's Activity: the log verifies again."
        );
        AuditChainCheck chain = await new AuditManager(
            database,
            TimeProvider.System
        ).VerifyChainAsync(samId.Value);
        Console.WriteLine(
            chain.Intact
                ? $"audit log: intact, {chain.EventCount} events, head {chain.HeadHash}"
                : $"audit log: BROKEN at event {chain.FirstBrokenId} of {chain.EventCount}"
        );
        return 0;
    }

    private static Db? OpenDatabase()
    {
        return ToolDatabase.Open();
    }
}
