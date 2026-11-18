using System.ComponentModel;
using EZEHR.Tools.Cli;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands.Patient;

public sealed class PatientSettings : CommandSettings
{
    [CommandArgument(0, "<patient>")]
    [Description("An EZEHR patient id, `sam`, or <provider>:<chart> such as lakeside:LUC-20417")]
    public PatientReference Patient { get; init; } = null!;
}
