using EZEHR.Tools.Commands;
using EZEHR.Tools.Commands.Org;
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
    }

    private static int Report(Exception exception)
    {
        IAnsiConsole console = AnsiConsole.Console;
        if (exception is CommandAppException commandLineError)
        {
            if (commandLineError.Pretty is not null)
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
