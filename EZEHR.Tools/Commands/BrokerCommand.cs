using EZEHR.Tools.Net;

namespace EZEHR.Tools.Commands;

/// <summary>
/// The data broker from the video: asks for a patient's whole record claiming "treatment", with no certificate
/// or with its own self-made one. Prints the server's answer verbatim, big enough to read on camera.
/// </summary>
public static class BrokerCommand
{
    public const string Usage = """
          broker request <patientId> --cert none|self
        """;

    public const string ClaimedPurpose = "treatment";

    public static async Task<int> RunAsync(string[] args)
    {
        string? certificateMode = CommandLine.Option(args, "--cert");
        if (
            args.Length < 2
            || args[0] != "request"
            || !Guid.TryParse(args[1], out Guid patientId)
            || certificateMode is not ("none" or "self")
        )
        {
            Console.Error.WriteLine($"usage:\n{Usage}");
            return 1;
        }

        string? pfxPath = certificateMode == "self" ? ToolPaths.Pki("broker.pfx") : null;
        using HttpClient client = NetworkClient.Create(NetworkClient.BaseUrl, pfxPath);
        string path = $"api/network/patients/{patientId}/read-full-record?purpose={ClaimedPurpose}";
        using HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();

        bool useColor = !Console.IsOutputRedirected;
        string rule = new('=', 72);
        Console.WriteLine(rule);
        Console.WriteLine($"  GET /{path}");
        Console.WriteLine(
            certificateMode == "self"
                ? $"  certificate: self-signed CN={CertsCommand.BrokerCommonName}"
                : "  certificate: none"
        );
        Console.WriteLine();
        if (useColor && !response.IsSuccessStatusCode)
        {
            Console.ForegroundColor = ConsoleColor.Red;
        }
        Console.WriteLine($"  HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        Console.WriteLine($"  {body}");
        if (useColor)
        {
            Console.ResetColor();
        }
        Console.WriteLine(rule);
        return 0;
    }
}
