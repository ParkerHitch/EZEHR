using Spectre.Console;

namespace EZEHR.Tools.Commands.Demo;

/// <summary>The stage's key presses: one key at a terminal, or one line per key for a scripted rehearsal.</summary>
public static class StageKeys
{
    /// <summary>Shows the prompt, waits for one of the allowed keys, then erases the prompt so it isn't recorded.</summary>
    public static char Wait(string prompt, string allowedKeys)
    {
        if (Console.IsInputRedirected)
        {
            return ReadScripted(Console.In, allowedKeys);
        }
        AnsiConsole.Console.Write(new Text($"  {prompt}", new Style(Color.Grey)));
        char key;
        do
        {
            ConsoleKeyInfo pressed = Console.ReadKey(intercept: true);
            key = pressed.Key == ConsoleKey.Enter ? '\r' : char.ToLowerInvariant(pressed.KeyChar);
        } while (!allowedKeys.Contains(key));
        AnsiConsole.Console.Write(new Text("\r" + new string(' ', prompt.Length + 2) + "\r"));
        return key;
    }

    /// <summary>
    /// One key per line: an empty line is Enter, a key this prompt doesn't take is skipped, and the end of the input
    /// is q.
    /// </summary>
    public static char ReadScripted(TextReader input, string allowedKeys)
    {
        while (input.ReadLine() is string line)
        {
            char typed = line.Length == 0 ? '\r' : char.ToLowerInvariant(line[0]);
            if (allowedKeys.Contains(typed))
            {
                return typed;
            }
        }
        return 'q';
    }
}
