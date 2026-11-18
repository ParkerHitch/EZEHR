using Spectre.Console;

namespace EZEHR.Tools;

/// <summary>
/// ezehr-tools: laptop-side commands. An explicit class rather than top-level statements, whose generated Program
/// would be public and clash with the web app's Program in the test project.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Piped output shouldn't wrap at all, so scripts and grep see whole lines; tables only take the width they need.
    /// COLUMNS overrides it.
    /// </summary>
    private const int RedirectedWidth = 1000;

    public static async Task<int> Main(string[] args)
    {
        // Piped output reports whatever width .NET guesses: about zero with no TERM (Spectre then squeezes everything
        // to "…"), or terminfo's 80 with TERM set, as on CI runners. Neither is a real terminal, so don't wrap.
        if (Console.IsOutputRedirected || AnsiConsole.Console.Profile.Width < 20)
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
