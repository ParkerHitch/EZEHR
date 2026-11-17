using System.Diagnostics;
using EZEHR.Tools;

namespace EZEHR.Tests.Tools;

/// <summary>The tool as a separate process with its output piped, as in scripts, $(...) and tee.</summary>
[Collection(ToolConsoleCollection.Name)]
public sealed class RedirectedOutputTests
{
    /// <summary>Runs the tool as its own process, with stdin piped from the given text.</summary>
    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        IReadOnlyDictionary<string, string?> environment,
        string standardInput,
        params string[] args
    )
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(ToolApp).Assembly.Location);
        foreach (string argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (KeyValuePair<string, string?> variable in environment)
        {
            start.Environment[variable.Key] = variable.Value;
        }
        using Process process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(standardInput);
        process.StandardInput.Close();
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + await error);
    }

    [Fact]
    public async Task ResetWithPipedAnswerDoesNothing()
    {
        (int exitCode, string output) = await RunProcessAsync(
            new Dictionary<string, string?>
            {
                ["AZURE_SQL_CONNECTIONSTRING"] = "Server=127.0.0.1,1;Database=nowhere",
            },
            "nope\n",
            "demo",
            "reset"
        );

        Assert.Equal(1, exitCode);
        Assert.Contains("Nothing was deleted.", output);
        Assert.DoesNotContain("Exception", output);
    }

    [Fact]
    public async Task StageUsesBaseUrlOption()
    {
        string pkiDirectory = ToolRunner.NewPkiDirectory();
        Dictionary<string, string?> environment = new()
        {
            ["EZEHR_PKI_DIRECTORY"] = pkiDirectory,
            ["EZEHR_BASE_URL"] = "https://localhost:7189",
        };
        await RunProcessAsync(environment, "", "certs", "issue");

        (int exitCode, string output) = await RunProcessAsync(
            environment,
            "\nq\n",
            "demo",
            "stage",
            "--from",
            "3",
            "--base-url",
            "https://127.0.0.1:1"
        );

        Assert.Equal(1, exitCode);
        Assert.Contains("EZEHR network: https://127.0.0.1:1", output);
        Assert.Contains("couldn't reach EZEHR at https://127.0.0.1:1", output);
        Assert.DoesNotContain("localhost:7189", output);
    }

    [Fact]
    public async Task RedirectedHelpIsReadable()
    {
        string toolPath = typeof(ToolApp).Assembly.Location;
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(toolPath);
        start.ArgumentList.Add("--help");
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Show where the tools point and what they hold", output);
    }

    [Fact]
    public async Task RedirectedLongLinesDontWrap()
    {
        string longDirectory = Path.Combine(Path.GetTempPath(), new string('p', 220));
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(ToolApp).Assembly.Location);
        start.ArgumentList.Add("env");
        start.Environment["EZEHR_PKI_DIRECTORY"] = longDirectory;
        start.Environment["EZEHR_BASE_URL"] = "https://127.0.0.1:1";
        start.Environment.Remove("COLUMNS");
        // With TERM set and no terminal, .NET reports terminfo's width (80 for xterm), as on CI runners.
        start.Environment["TERM"] = "xterm";
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Contains(longDirectory, output);
    }
}
