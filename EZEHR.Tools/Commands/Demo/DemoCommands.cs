using System.ComponentModel;
using Dapper;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Demo;

public sealed class DemoResetSettings : CommandSettings
{
    [CommandOption("--yes")]
    [Description("Don't ask to type reset first")]
    public bool Confirmed { get; init; }
}

public sealed class DemoStageSettings : CertificateAuthoritySettings
{
    [CommandOption("--from <step>")]
    [Description("Start at this step number (see --list)")]
    [DefaultValue(1)]
    public int FirstStep { get; init; } = 1;

    [CommandOption("--list")]
    [Description("Print the steps, numbered, and call nothing")]
    public bool List { get; init; }

    public override ValidationResult Validate()
    {
        ValidationResult authority = base.Validate();
        if (!authority.Successful)
        {
            return authority;
        }
        int count = StageSteps.Count;
        return FirstStep < 1 || FirstStep > count
            ? ValidationResult.Error($"--from takes a step number from 1 to {count}")
            : ValidationResult.Success();
    }
}

/// <summary>
/// Empties the database before a run, and takes Northwind and Medication Check off the network: the stage has both
/// join it on camera.
/// </summary>
public sealed class DemoResetCommand : AsyncCommand<DemoResetSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DemoResetSettings settings,
        CancellationToken cancellationToken
    )
    {
        Db database = ToolDatabase.Open();
        IAnsiConsole console = AnsiConsole.Console;
        console.WriteLine($"site:     {NetworkClient.BaseUrl}");
        console.WriteLine($"database: {ToolDatabase.Describe()}");
        if (!settings.Confirmed)
        {
            const string question =
                "This deletes every patient, record, grant and audit event there, and the Northwind and Medication Check organizations. Type reset:";
            string typed;
            if (Console.IsInputRedirected)
            {
                // A prompt can't read piped input; a script answers on stdin, or passes --yes.
                console.WriteLine(question);
                typed = Console.In.ReadLine() ?? "";
            }
            else
            {
                typed = console.Prompt(new TextPrompt<string>(question).AllowEmpty());
            }
            if (typed.Trim() != "reset")
            {
                console.WriteLine("Nothing was deleted.");
                return 1;
            }
        }
        await database.DeleteAllDataAsync();
        // Stage has Northwind and Medication Check join the network, so both have to start off it. Only their rows
        // go: the other organizations, and any sysadmin edits to them (Riverside's tenant), stay.
        await using SqlConnection connection = await database.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM organizations WHERE uri IN (@northwindUri, @appUri)",
            new
            {
                northwindUri = OrgDirectory.ProviderUri(StageSteps.JoiningProviderSlug),
                appUri = OrgDirectory.AppUri(StageSteps.JoiningAppSlug),
            }
        );
        console.WriteLine(
            "Database emptied, and Northwind and Medication Check are off the network until the stage adds them. Next: ezehr-tools demo stage"
        );
        return 0;
    }
}

/// <summary>
/// Flips granted on the broker's first refused request to Sam, straight in the database, so the log now says the
/// broker got in. The fingerprints no longer match, and Sam's Activity shows the warning on its next load.
/// Running it again restores the event exactly, and the same fingerprint comes back.
/// </summary>
public sealed class DemoTamperCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CancellationToken cancellationToken
    )
    {
        Guid samId = await PatientReference.Sam.ResolveAsync();
        Db database = ToolDatabase.Open();
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
            throw new ToolException(
                "No refused broker request to edit yet: run the stage's broker step first."
            );
        }
        bool nowGranted = await connection.ExecuteScalarAsync<bool>(
            """
            UPDATE audit_events SET granted = 1 - granted OUTPUT inserted.granted
            WHERE id = @eventId
            """,
            new { eventId }
        );
        IAnsiConsole console = AnsiConsole.Console;
        console.WriteLine(
            nowGranted
                ? $"Audit event {eventId} now says the broker's request was granted. Reload Sam's Activity: the log shows as changed."
                : $"Audit event {eventId} is back as it was (refused). Reload Sam's Activity: the log verifies again."
        );
        AuditChainCheck chain = await new AuditManager(
            database,
            TimeProvider.System
        ).VerifyChainAsync(samId);
        console.WriteLine(
            chain.Intact
                ? $"audit log: intact, {chain.EventCount} events, head {chain.HeadHash}"
                : $"audit log: BROKEN at event {chain.FirstBrokenId} of {chain.EventCount}"
        );
        return 0;
    }
}

/// <summary>The demo's network calls in order, one per key press.</summary>
public sealed class DemoStageCommand : AsyncCommand<DemoStageSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DemoStageSettings settings,
        CancellationToken cancellationToken
    )
    {
        if (settings.List)
        {
            StageSteps.List();
            return 0;
        }
        return await StageSteps.RunAsync(settings, settings.FirstStep);
    }
}
