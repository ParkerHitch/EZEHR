using System.ComponentModel;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Calls;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Network;

/// <summary>
/// Any read of a patient's record, as any identity: the building block of a manual check. With --expect, the exit
/// code says whether the server answered as expected.
/// </summary>
public sealed class NetworkReadCommand : AsyncCommand<NetworkReadCommand.Settings>
{
    public sealed class Settings : NetworkSettings
    {
        [CommandArgument(0, "<patient>")]
        [Description(
            "An EZEHR patient id, `sam`, or <provider>:<chart> such as lakeside:LUC-20417"
        )]
        public PatientReference Patient { get; init; } = null!;

        [CommandArgument(1, "<record>")]
        [Description("What to read: egfr-labs, medications or full-record")]
        public string Record { get; init; } = "";

        [CommandOption("--as <identity>")]
        [Description(
            "Whose certificate to present: none, broker, platform, an organization's slug, or a .pfx path"
        )]
        public IdentityReference? Identity { get; init; }

        [CommandOption("--purpose <purpose>")]
        [Description("With full-record: the purpose the caller claims, such as treatment")]
        public string? Purpose { get; init; }

        [CommandOption("--expect <outcome>")]
        [Description(
            "ok, an HTTP status such as 403, or a deny reason such as ScopeDenied; exits 1 when it doesn't match"
        )]
        public ExpectedOutcome? Expect { get; init; }

        [CommandOption("--raw")]
        [Description("Print the response body as it came, instead of the table of entries")]
        public bool Raw { get; init; }

        public RecordKind Kind
        {
            get { return RecordReads.Names[Record]; }
        }

        public override ValidationResult Validate()
        {
            if (!RecordReads.Names.ContainsKey(Record))
            {
                return ValidationResult.Error(
                    $"`{Record}` isn't a record: use {string.Join(", ", RecordReads.Names.Keys)}"
                );
            }
            if (Identity is null)
            {
                return ValidationResult.Error(
                    "--as names whose certificate the call presents: none, broker, an organization's slug such as lakeside, or a .pfx path"
                );
            }
            if (Purpose is not null && Kind != RecordKind.FullRecord)
            {
                return ValidationResult.Error("--purpose only goes with full-record");
            }
            return ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken
    )
    {
        Guid patientId = await settings.Patient.ResolveAsync();
        IdentityReference identity = settings.Identity!;
        string? pfxPath = identity.PfxPath is null ? null : identity.RequirePfx();
        using HttpClient client = NetworkClient.Create(settings.BaseUrl, pfxPath);
        ReadOutcome outcome = await RecordReads.ReadAsync(
            client,
            patientId,
            settings.Kind,
            settings.Purpose,
            $"certificate: {identity.Describe()}"
        );
        if (outcome.Succeeded && settings.Raw)
        {
            AnsiConsole.Console.WriteLine(outcome.Body);
        }
        else if (outcome.Succeeded)
        {
            WriteEntries(outcome.Entries);
        }
        if (settings.Expect is null)
        {
            return outcome.Succeeded ? 0 : 1;
        }
        if (settings.Expect.Matches(outcome))
        {
            NetworkLog.WriteInColor(Color.Green, $"  as expected: {settings.Expect}");
            return 0;
        }
        NetworkLog.WriteInColor(
            Color.Red,
            $"  expected {settings.Expect}, got {outcome.Describe()}"
        );
        return 1;
    }

    /// <summary>Each entry with who signed it, as this laptop's own check of the signature says.</summary>
    private static void WriteEntries(IReadOnlyList<SignedEntry> entries)
    {
        Table table = new Table()
            .AddColumn("type")
            .AddColumn("date")
            .AddColumn("id")
            .AddColumn("signature");
        foreach (CheckedEntry checkedEntry in SignatureCheck.Verify(entries))
        {
            table.AddRow(
                new Text(checkedEntry.Entry.ResourceType),
                new Text(RecordReads.ClinicalDate(checkedEntry.Entry.Resource)),
                new Text(checkedEntry.Entry.Id.ToString()),
                checkedEntry.Verified is null
                    ? new Text($"FAILED: {checkedEntry.Failure}", new Style(Color.Red))
                    : new Text(Copy.SignedBy(checkedEntry.Verified.Signer.OrgName))
            );
        }
        AnsiConsole.Console.Write(table);
    }
}
