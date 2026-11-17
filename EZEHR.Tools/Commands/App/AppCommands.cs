using System.ComponentModel;
using EZEHR.Tools.Apps;
using EZEHR.Tools.Cli;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.App;

public sealed class AppSettings : NetworkSettings
{
    [CommandArgument(0, "<patient>")]
    [Description("An EZEHR patient id, `sam`, or <provider>:<chart> such as lakeside:LUC-20417")]
    public PatientReference Patient { get; init; } = null!;
}

public sealed class AppKidneyCheckCommand : AsyncCommand<AppSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        AppSettings settings,
        CancellationToken cancellationToken
    )
    {
        Guid patientId = await settings.Patient.ResolveAsync();
        return await KidneyCheckApp.RunAsync(settings.BaseUrl, patientId) ? 0 : 1;
    }
}

public sealed class AppMedicationCheckCommand : AsyncCommand<AppSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        AppSettings settings,
        CancellationToken cancellationToken
    )
    {
        Guid patientId = await settings.Patient.ResolveAsync();
        return await MedicationCheckApp.RunAsync(settings.BaseUrl, patientId) ? 0 : 1;
    }
}
