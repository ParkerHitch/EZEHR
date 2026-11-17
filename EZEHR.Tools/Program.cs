using Spectre.Console;

namespace EZEHR.Tools;

/// <summary>
/// ezehr-tools: laptop-side commands. An explicit class rather than top-level statements, whose generated Program
/// would be public and clash with the web app's Program in the test project.
/// </summary>
internal static class Program
{
    /// <summary>Wide enough that piped tables don't wrap; COLUMNS overrides it.</summary>
    private const int RedirectedWidth = 160;

    public static async Task<int> Main(string[] args)
    {
        // With output piped, the console reports a width of about zero and Spectre squeezes everything to "…".
        if (AnsiConsole.Console.Profile.Width < 20)
        {
            AnsiConsole.Console.Profile.Width = int.TryParse(
                Environment.GetEnvironmentVariable("COLUMNS"),
                out int columns
            )
                ? columns
                : RedirectedWidth;
        }
        return await ToolApp.RunAsync(args);
    }
}
