using System.ComponentModel;
using EZEHR.Services;
using EZEHR.Tools.Calls;
using EZEHR.Tools.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Certs;

/// <summary>A new organization's slug, which names its CN and its .pfx.</summary>
public sealed class CertsOrganizationSettings : CertificateAuthoritySettings
{
    [CommandArgument(0, "<slug>")]
    [Description("The organization's slug: lowercase letters, digits and hyphens, such as harbor")]
    public string Slug { get; init; } = "";

    public override ValidationResult Validate()
    {
        return OrganizationValidation.All(base.Validate(), OrganizationValidation.Slug(Slug));
    }
}

public sealed class CertsIssueCommand : AsyncCommand<CertificateAuthoritySettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CertificateAuthoritySettings settings,
        CancellationToken cancellationToken
    )
    {
        await CertificateIssuance.IssueDemoOrganizationsAsync(settings);
        return 0;
    }
}

public sealed class CertsPlatformCommand : AsyncCommand<CertificateAuthoritySettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CertificateAuthoritySettings settings,
        CancellationToken cancellationToken
    )
    {
        string thumbprint = await CertificateIssuance.IssuePlatformAsync(settings);
        IAnsiConsole console = AnsiConsole.Console;
        console.WriteLine();
        console.WriteLine("Pin it on the server, then restart it:");
        console.WriteLine(
            $"  App Service setting:  App__PlatformCertificateThumbprint = {thumbprint}"
        );
        console.WriteLine(
            $"  locally, in the server's shell:  export App__PlatformCertificateThumbprint={thumbprint}"
        );
        return 0;
    }
}

public sealed class CertsProviderCommand : AsyncCommand<CertsOrganizationSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CertsOrganizationSettings settings,
        CancellationToken cancellationToken
    )
    {
        await CertificateIssuance.IssueOrganizationAsync(
            settings,
            OrgDirectory.ProviderCommonName(settings.Slug)
        );
        AnsiConsole.Console.WriteLine(
            $"EZEHR doesn't know {settings.Slug} yet. To admit it: ezehr-tools platform register-provider {settings.Slug} --name \"<display name>\""
        );
        return 0;
    }
}

public sealed class CertsAppCommand : AsyncCommand<CertsOrganizationSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CertsOrganizationSettings settings,
        CancellationToken cancellationToken
    )
    {
        await CertificateIssuance.IssueOrganizationAsync(
            settings,
            OrgDirectory.AppCommonName(settings.Slug)
        );
        AnsiConsole.Console.WriteLine(
            $"EZEHR doesn't know {settings.Slug} yet. To admit it: ezehr-tools platform register-app {settings.Slug} --name … --scope … --description …"
        );
        return 0;
    }
}

public sealed class CertsBrokerCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        CertificateIssuance.WriteBroker();
        return 0;
    }
}
