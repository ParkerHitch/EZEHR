using System.ComponentModel;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Calls;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Provider;

/// <summary>Which provider's EHR is acting: a demo provider or one onboarded from this laptop.</summary>
public class ProviderSettings : NetworkSettings
{
    [CommandArgument(0, "<provider>")]
    [Description("The provider acting, such as lakeside: it presents .pki/<provider>.pfx")]
    public string Provider { get; init; } = "";

    public override ValidationResult Validate()
    {
        List<string> providers = ToolOrganizations
            .All()
            .Where(organization => organization.Kind == PartyKind.Provider)
            .Select(ToolOrganizations.Slug)
            .ToList();
        return providers.Contains(Provider)
            ? ValidationResult.Success()
            : ValidationResult.Error(
                $"`{Provider}` isn't a provider the tools know: use {string.Join(", ", providers)}, or onboard one with `ezehr-tools platform onboard-provider`"
            );
    }

    /// <summary>A client presenting the provider's certificate.</summary>
    public HttpClient CreateClient()
    {
        return NetworkClient.Create(BaseUrl, IdentityReference.Parse(Provider).RequirePfx());
    }
}

public class ChartSettings : ProviderSettings
{
    [CommandArgument(1, "<chart>")]
    [Description("The provider's own patient id (its chart number), such as LUC-20417")]
    public string Chart { get; init; } = "";
}

public sealed class PushSettings : ChartSettings
{
    [CommandOption("--file <path>")]
    [Description(
        "Push these FHIR resources instead of the seed records: one resource, an array, or the seed format"
    )]
    public string? FilePath { get; init; }

    [CommandOption("--sign-as <identity>")]
    [Description(
        "Sign with this identity's certificate instead of the provider's, to test a wrong signer"
    )]
    public IdentityReference? SignAs { get; init; }

    [CommandOption("--corrupt-signature")]
    [Description("Flip a byte of each record's signature after signing, to test a broken one")]
    public bool CorruptSignature { get; init; }
}

public sealed class LaunchSettings : ChartSettings
{
    [CommandOption("--clinician <object-id>")]
    [Description(
        "The clinician's object id in the provider's Entra tenant (Entra admin center, Users)"
    )]
    public Guid? ClinicianObjectId { get; init; }

    [CommandOption("--encounter <id>")]
    [Description("The visit the launch is for. Default: visit-<yyyyMMdd-HHmm>, now")]
    public string? EncounterId { get; init; }

    public override ValidationResult Validate()
    {
        ValidationResult provider = base.Validate();
        if (!provider.Successful)
        {
            return provider;
        }
        return ClinicianObjectId is null
            ? ValidationResult.Error(
                "--clinician takes the clinician's object id in the provider's Entra tenant (Entra admin center, Users)"
            )
            : ValidationResult.Success();
    }
}

public sealed class AuditSettings : ProviderSettings
{
    [CommandOption("--clinician <uri>")]
    [Description("Only this clinician's events, by URI (from `provider clinicians`)")]
    public string? ClinicianUri { get; init; }

    [CommandOption("--chart <chart>")]
    [Description("Only events for the patient with this chart at the provider, such as LUC-20417")]
    public string? Chart { get; init; }

    [CommandOption("--patient <patient>")]
    [Description("Only events for this patient: an EZEHR patient id, `sam`, or <provider>:<chart>")]
    public PatientReference? Patient { get; init; }

    [CommandOption("--since <time>")]
    [Description("Only events at or after this time, such as 2026-11-17 or 2026-11-17T09:00-05:00")]
    public DateTimeOffset? Since { get; init; }

    [CommandOption("--until <time>")]
    [Description("Only events before this time")]
    public DateTimeOffset? Until { get; init; }

    [CommandOption("--limit <count>")]
    [Description("Events per page, 1 to 500")]
    [DefaultValue(ProviderCalls.DefaultAuditLimit)]
    public int Limit { get; init; } = ProviderCalls.DefaultAuditLimit;

    public override ValidationResult Validate()
    {
        ValidationResult provider = base.Validate();
        if (!provider.Successful)
        {
            return provider;
        }
        return Limit is < 1 or > 500
            ? ValidationResult.Error("--limit takes 1 to 500")
            : ValidationResult.Success();
    }
}

public sealed class ProviderInviteCommand : AsyncCommand<ChartSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        ChartSettings settings,
        CancellationToken cancellationToken
    )
    {
        using HttpClient client = settings.CreateClient();
        return await ProviderCalls.InviteAsync(client, settings.Chart) ? 0 : 1;
    }
}

public sealed class ProviderPushCommand : AsyncCommand<PushSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        PushSettings settings,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<ResourceVisit> visits = settings.FilePath is null
            ? ResourceFile.LoadSeed(settings.Provider)
            : ResourceFile.Load(settings.FilePath);
        IdentityReference signer = settings.SignAs ?? IdentityReference.Parse(settings.Provider);
        using HttpClient client = settings.CreateClient();
        bool pushed = await ProviderCalls.PushAsync(
            client,
            PfxFile.Load(signer.RequirePfx()),
            settings.Chart,
            visits,
            settings.CorruptSignature
        );
        return pushed ? 0 : 1;
    }
}

public sealed class ProviderLaunchCommand : AsyncCommand<LaunchSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        LaunchSettings settings,
        CancellationToken cancellationToken
    )
    {
        using HttpClient client = settings.CreateClient();
        bool launched = await ProviderCalls.LaunchAsync(
            client,
            settings.Chart,
            settings.ClinicianObjectId!.Value,
            settings.EncounterId ?? $"visit-{DateTime.Now:yyyyMMdd-HHmm}"
        );
        return launched ? 0 : 1;
    }
}

public sealed class ProviderAuditCommand : AsyncCommand<AuditSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        AuditSettings settings,
        CancellationToken cancellationToken
    )
    {
        Guid? patientId = settings.Patient is null ? null : await settings.Patient.ResolveAsync();
        using HttpClient client = settings.CreateClient();
        bool read = await ProviderCalls.ReadAuditAsync(
            client,
            new OrganizationAuditQuery(
                settings.ClinicianUri,
                patientId,
                settings.Chart,
                settings.Since,
                settings.Until,
                settings.Limit
            )
        );
        return read ? 0 : 1;
    }
}

public sealed class ProviderCliniciansCommand : AsyncCommand<ProviderSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        ProviderSettings settings,
        CancellationToken cancellationToken
    )
    {
        using HttpClient client = settings.CreateClient();
        return await ProviderCalls.ListCliniciansAsync(client) ? 0 : 1;
    }
}
