using System.Diagnostics;
using EZEHR.Tools;

namespace EZEHR.Tests.Tools;

/// <summary>The tool as a separate process with its output piped, as in scripts, $(...) and tee.</summary>
[Collection(ToolConsoleCollection.Name)]
public sealed class RedirectedOutputTests
{
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
}
