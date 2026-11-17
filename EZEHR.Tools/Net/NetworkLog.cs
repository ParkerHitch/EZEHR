using Spectre.Console;

namespace EZEHR.Tools.Net;

/// <summary>
/// Prints each network call the same way, so whoever watches can see the API: "→ POST /api/network/..." before
/// it's sent, then "← 201 Created ..." in green, or the status and the server's error body in red. Text is never
/// read as markup: server bodies are full of brackets.
/// </summary>
public static class NetworkLog
{
    public static void Request(string method, string path, string detail)
    {
        AnsiConsole.Console.WriteLine($"  → {method} /{path}   {detail}".TrimEnd());
    }

    public static void Succeeded(HttpResponseMessage response, string detail)
    {
        WriteInColor(Color.Green, $"  ← {Status(response)}   {detail}".TrimEnd());
    }

    /// <summary>The status and the server's body as-is, e.g. 409 Conflict {"error":"PatientNotLinked"}.</summary>
    public static async Task FailedAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        WriteInColor(Color.Red, $"  ← {Status(response)}   {body}".TrimEnd());
    }

    public static void WriteInColor(Color color, string line)
    {
        AnsiConsole.Console.Write(new Text(line, new Style(color)));
        AnsiConsole.Console.WriteLine();
    }

    private static string Status(HttpResponseMessage response)
    {
        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }
}
