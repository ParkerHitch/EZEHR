using EZEHR.Tools;
using Spectre.Console;
using Spectre.Console.Testing;

namespace EZEHR.Tests.Tools;

/// <summary>AnsiConsole.Console and environment variables are process-wide, so the tools tests run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ToolConsoleCollection
{
    public const string Name = "Tools console";
}

public sealed record ToolRun(int ExitCode, string Output);

/// <summary>
/// Runs ezehr-tools in-process with its output captured, the given environment variables set (null unsets one), and
/// a fresh .pki directory unless the caller names one.
/// </summary>
public static class ToolRunner
{
    public const string PkiDirectoryVariable = "EZEHR_PKI_DIRECTORY";

    public static Task<ToolRun> RunAsync(params string[] args)
    {
        return RunAsync(new Dictionary<string, string?>(), args);
    }

    public static async Task<ToolRun> RunAsync(
        IReadOnlyDictionary<string, string?> environment,
        params string[] args
    )
    {
        Dictionary<string, string?> applied = new(environment);
        if (!applied.ContainsKey(PkiDirectoryVariable))
        {
            applied[PkiDirectoryVariable] = NewPkiDirectory();
        }
        Dictionary<string, string?> previousValues = applied.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable
        );
        TestConsole console = new();
        console.Profile.Width = 200;
        IAnsiConsole previousConsole = AnsiConsole.Console;
        try
        {
            foreach (KeyValuePair<string, string?> variable in applied)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
            AnsiConsole.Console = console;
            int exitCode = await ToolApp.RunAsync(args);
            return new ToolRun(exitCode, console.Output);
        }
        finally
        {
            AnsiConsole.Console = previousConsole;
            foreach (KeyValuePair<string, string?> variable in previousValues)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
        }
    }

    /// <summary>An empty directory for one test's certificates.</summary>
    public static string NewPkiDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ezehr-tools-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
