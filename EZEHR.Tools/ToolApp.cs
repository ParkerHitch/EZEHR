using EZEHR.Tools.Commands;
using EZEHR.Tools.Commands.Database;
using EZEHR.Tools.Commands.Org;
using EZEHR.Tools.Commands.Patient;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools;

/// <summary>
/// The whole command tree, with every description and example. Exit codes: 0 done, 1 the work failed, 2 the command
/// line was wrong.
/// </summary>
public static class ToolApp
{
    public const int FailedExitCode = 1;

    public const int UsageExitCode = 2;

    public static Task<int> RunAsync(string[] args)
    {
        CommandApp app = new();
        app.Configure(Configure);
        return app.RunAsync(args);
    }

    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("ezehr-tools");
        config.ConfigureConsole(AnsiConsole.Console);
        config.UseStrictParsing();
        config.ValidateExamples();
        config.SetExceptionHandler(
            (exception, _) =>
            {
                return Report(exception);
            }
        );

        config
            .AddCommand<EnvCommand>("env")
            .WithDescription("Show where the tools point and what they hold")
            .WithExample("env")
            .WithExample("env", "--base-url", "https://ezehr.example.com");
        config.AddBranch("org", org =>
        {
            org.SetDescription("The organizations the tools know, from .pki/ and the demo list");
            org.AddCommand<OrgListCommand>("list")
                .WithDescription("List each organization with its certificate and seed records")
                .WithExample("org", "list");
        });
        config.AddBranch("patient", patient =>
        {
            patient.SetDescription(
                "Backstage: look patients up in the database (AZURE_SQL_CONNECTIONSTRING), never the network"
            );
            patient
                .AddCommand<PatientFindCommand>("find")
                .WithDescription("Print a patient's EZEHR id, for $(...) in scripts")
                .WithExample("patient", "find", "lakeside:LUC-20417")
                .WithExample("patient", "find", "sam");
            patient
                .AddCommand<PatientListCommand>("list")
                .WithDescription("List every enrolled patient with their linked charts")
                .WithExample("patient", "list");
            patient
                .AddCommand<PatientStatusCommand>("status")
                .WithDescription("Show a record's contents, grants and audit log check")
                .WithExample("patient", "status", "sam")
                .WithExample("patient", "status", "riverside:RPC-1107");
        });
        config.AddBranch("db", db =>
        {
            db.SetDescription(
                "Backstage: SQL straight against the database (AZURE_SQL_CONNECTIONSTRING)"
            );
            db.AddCommand<DbQueryCommand>("query")
                .WithDescription("Run SQL and print the rows as a table")
                .WithExample("db", "query", "SELECT id, action, granted FROM audit_events");
        });
    }

    private static int Report(Exception exception)
    {
        IAnsiConsole console = AnsiConsole.Console;
        if (exception is CommandAppException commandLineError)
        {
            // A value our own parsers refused (a patient, an identity): their message says what would work.
            if (commandLineError.InnerException is FormatException formatError)
            {
                console.Write(new Text(formatError.Message, new Style(Color.Red)));
                console.WriteLine();
            }
            else if (commandLineError.Pretty is not null)
            {
                console.Write(commandLineError.Pretty);
            }
            else
            {
                console.Write(new Text(commandLineError.Message, new Style(Color.Red)));
                console.WriteLine();
            }
            return UsageExitCode;
        }
        if (exception is ToolException expected)
        {
            console.Write(new Text(expected.Message, new Style(Color.Red)));
            console.WriteLine();
            return FailedExitCode;
        }
        console.WriteException(exception, ExceptionFormats.ShortenEverything);
        return FailedExitCode;
    }
}
