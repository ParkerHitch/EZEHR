using System.ComponentModel;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Calls;
using EZEHR.Tools.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Platform;

public sealed class RegisterProviderSettings : NetworkSettings
{
    [CommandArgument(0, "<slug>")]
    [Description("The provider's slug, such as harbor: its CN is <slug>.providers.ezehr")]
    public string Slug { get; init; } = "";

    [CommandOption("--name <name>")]
    [Description("The display name patients and clinicians see, such as \"Harbor Clinic\"")]
    public string? Name { get; init; }

    public override ValidationResult Validate()
    {
        return OrganizationValidation.All(
            OrganizationValidation.Slug(Slug),
            OrganizationValidation.Name(Name)
        );
    }
}

public sealed class RegisterAppSettings : NetworkSettings
{
    [CommandArgument(0, "<slug>")]
    [Description("The app's slug, such as sleep-check: its CN is <slug>.apps.ezehr")]
    public string Slug { get; init; } = "";

    [CommandOption("--name <name>")]
    [Description("The display name patients see on their Access page")]
    public string? Name { get; init; }

    [CommandOption("--scope <scope>")]
    [Description("What a grant to the app covers: LabsAndNotes or MedicationsLabsAndNotes")]
    public GrantScope? Scope { get; init; }

    [CommandOption("--description <text>")]
    [Description("The sentence patients read before turning the app on")]
    public string? AppDescription { get; init; }

    public override ValidationResult Validate()
    {
        return OrganizationValidation.All(
            OrganizationValidation.Slug(Slug),
            OrganizationValidation.Name(Name),
            OrganizationValidation.AppScope(Scope, AppDescription)
        );
    }
}

public sealed class OnboardProviderSettings : CertificateAuthoritySettings
{
    [CommandArgument(0, "<slug>")]
    [Description("The provider's slug, such as harbor: its CN is <slug>.providers.ezehr")]
    public string Slug { get; init; } = "";

    [CommandOption("--name <name>")]
    [Description("The display name patients and clinicians see, such as \"Harbor Clinic\"")]
    public string? Name { get; init; }

    [CommandOption("--invite <chart>")]
    [Description("Then ask for an enrollment invite for this chart, as the new provider")]
    public string? InviteChart { get; init; }

    public override ValidationResult Validate()
    {
        return OrganizationValidation.All(
            base.Validate(),
            OrganizationValidation.Slug(Slug),
            OrganizationValidation.Name(Name)
        );
    }
}

public sealed class OnboardAppSettings : CertificateAuthoritySettings
{
    [CommandArgument(0, "<slug>")]
    [Description("The app's slug, such as sleep-check: its CN is <slug>.apps.ezehr")]
    public string Slug { get; init; } = "";

    [CommandOption("--name <name>")]
    [Description("The display name patients see on their Access page")]
    public string? Name { get; init; }

    [CommandOption("--scope <scope>")]
    [Description("What a grant to the app covers: LabsAndNotes or MedicationsLabsAndNotes")]
    public GrantScope? Scope { get; init; }

    [CommandOption("--description <text>")]
    [Description("The sentence patients read before turning the app on")]
    public string? AppDescription { get; init; }

    public override ValidationResult Validate()
    {
        return OrganizationValidation.All(
            base.Validate(),
            OrganizationValidation.Slug(Slug),
            OrganizationValidation.Name(Name),
            OrganizationValidation.AppScope(Scope, AppDescription)
        );
    }
}

public sealed class PlatformRegisterProviderCommand : AsyncCommand<RegisterProviderSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        RegisterProviderSettings settings,
        CancellationToken cancellationToken
    )
    {
        await PlatformCalls.RegisterProviderAsync(settings.BaseUrl, settings.Slug, settings.Name!);
        return 0;
    }
}

public sealed class PlatformRegisterAppCommand : AsyncCommand<RegisterAppSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        RegisterAppSettings settings,
        CancellationToken cancellationToken
    )
    {
        await PlatformCalls.RegisterAppAsync(
            settings.BaseUrl,
            settings.Slug,
            settings.Name!,
            settings.Scope!.Value,
            settings.AppDescription!
        );
        return 0;
    }
}

/// <summary>What onboarding a provider takes: its certificate from the CA, then the platform admits it.</summary>
public sealed class PlatformOnboardProviderCommand : AsyncCommand<OnboardProviderSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        OnboardProviderSettings settings,
        CancellationToken cancellationToken
    )
    {
        // Fail before the CA issues anything when the operator can't register the result.
        IdentityReference.Parse(IdentityReference.Platform).RequirePfx();
        await CertificateIssuance.IssueOrganizationAsync(
            settings,
            OrgDirectory.ProviderCommonName(settings.Slug)
        );
        await PlatformCalls.RegisterProviderAsync(settings.BaseUrl, settings.Slug, settings.Name!);
        if (settings.InviteChart is null)
        {
            AnsiConsole.Console.WriteLine(
                $"next: ezehr-tools provider invite {settings.Slug} <chart>"
            );
            return 0;
        }
        AnsiConsole.Console.WriteLine();
        return await ProviderCommand.RunAsync(["invite", settings.Slug, settings.InviteChart]);
    }
}

public sealed class PlatformOnboardAppCommand : AsyncCommand<OnboardAppSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        OnboardAppSettings settings,
        CancellationToken cancellationToken
    )
    {
        IdentityReference.Parse(IdentityReference.Platform).RequirePfx();
        await CertificateIssuance.IssueOrganizationAsync(
            settings,
            OrgDirectory.AppCommonName(settings.Slug)
        );
        await PlatformCalls.RegisterAppAsync(
            settings.BaseUrl,
            settings.Slug,
            settings.Name!,
            settings.Scope!.Value,
            settings.AppDescription!
        );
        return 0;
    }
}
