using EZEHR.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Org;

/// <summary>Every organization the tools can act as: the demo ones, and any onboarded from this laptop.</summary>
public sealed class OrgListCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        Table table = new Table()
            .AddColumn("slug")
            .AddColumn("kind")
            .AddColumn("name")
            .AddColumn("certificate CN")
            .AddColumn("certificate")
            .AddColumn("seed records");
        foreach (Organization organization in ToolOrganizations.All())
        {
            string slug = ToolOrganizations.Slug(organization);
            string pfxPath = ToolPaths.Pki($"{slug}.pfx");
            string certificate = File.Exists(pfxPath)
                ? $"expires {PfxFile.Load(pfxPath).Certificate.NotAfter:yyyy-MM-dd}"
                : "missing";
            string seed =
                organization.Kind != PartyKind.Provider ? ""
                : File.Exists(Path.Combine(ToolPaths.SeedDirectory, $"{slug}.json")) ? "yes"
                : "no";
            table.AddRow(
                new Text(slug),
                new Text(organization.Kind.ToString()),
                new Text(organization.DisplayName),
                new Text(organization.CertificateCommonName),
                new Text(certificate),
                new Text(seed)
            );
        }
        AnsiConsole.Console.Write(table);
        return 0;
    }
}
