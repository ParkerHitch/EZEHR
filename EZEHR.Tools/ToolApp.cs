using EZEHR.Tools.Cli;
using EZEHR.Tools.Commands;
using EZEHR.Tools.Commands.Certs;
using EZEHR.Tools.Commands.Database;
using EZEHR.Tools.Commands.Org;
using EZEHR.Tools.Commands.Patient;
using EZEHR.Tools.Commands.Platform;
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
        config.AddBranch("certs", certs =>
        {
            certs.SetDescription("Issue certificates into .pki/: the CA only, EZEHR isn't called");
            certs
                .AddCommand<CertsIssueCommand>("issue")
                .WithDescription("Issue every demo organization's certificate, plus trust files")
                .WithExample("certs", "issue")
                .WithExample("certs", "issue", "--ca", "ezca", "--key", "rsa");
            certs
                .AddCommand<CertsPlatformCommand>("platform")
                .WithDescription("Issue the platform operator's certificate, and print its pin")
                .WithExample("certs", "platform");
            certs
                .AddCommand<CertsProviderCommand>("provider")
                .WithDescription("Issue a new provider's certificate, not yet admitted to EZEHR")
                .WithExample("certs", "provider", "harbor");
            certs
                .AddCommand<CertsAppCommand>("app")
                .WithDescription("Issue a new app's certificate, not yet admitted to EZEHR")
                .WithExample("certs", "app", "sleep-check");
            certs
                .AddCommand<CertsBrokerCommand>("broker")
                .WithDescription("Make the data broker's self-signed certificate")
                .WithExample("certs", "broker");
        });
        config.AddBranch("platform", platform =>
        {
            platform.SetDescription(
                "The platform operator, with .pki/platform.pfx: admits providers and apps"
            );
            platform
                .AddCommand<PlatformRegisterProviderCommand>("register-provider")
                .WithDescription("Admit a provider whose certificate is already issued")
                .WithExample(
                    "platform",
                    "register-provider",
                    "harbor",
                    "--name",
                    "\"Harbor Clinic\""
                );
            platform
                .AddCommand<PlatformRegisterAppCommand>("register-app")
                .WithDescription("Admit an app whose certificate is already issued")
                .WithExample(
                    "platform",
                    "register-app",
                    "sleep-check",
                    "--name",
                    "\"Sleep Check\"",
                    "--scope",
                    "LabsAndNotes",
                    "--description",
                    "\"Looks at your sleep study results.\""
                );
            platform
                .AddCommand<PlatformOnboardProviderCommand>("onboard-provider")
                .WithDescription("Issue a provider's certificate and admit it, then invite")
                .WithExample(
                    "platform",
                    "onboard-provider",
                    "harbor",
                    "--name",
                    "\"Harbor Clinic\"",
                    "--invite",
                    "HBR-001"
                );
            platform
                .AddCommand<PlatformOnboardAppCommand>("onboard-app")
                .WithDescription("Issue an app's certificate and admit it")
                .WithExample(
                    "platform",
                    "onboard-app",
                    "sleep-check",
                    "--name",
                    "\"Sleep Check\"",
                    "--scope",
                    "LabsAndNotes",
                    "--description",
                    "\"Looks at your sleep study results.\""
                );
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
                .WithExample("db", "query", "\"SELECT id, action, granted FROM audit_events\"");
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
