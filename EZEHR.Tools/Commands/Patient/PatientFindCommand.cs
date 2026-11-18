using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Patient;

/// <summary>Prints only the EZEHR patient id, so scripts can use $(ezehr-tools patient find ...).</summary>
public sealed class PatientFindCommand : AsyncCommand<PatientSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        PatientSettings settings,
        CancellationToken cancellationToken
    )
    {
        Guid patientId = await settings.Patient.ResolveAsync();
        AnsiConsole.Console.WriteLine(patientId.ToString());
        return 0;
    }
}
