using System.Security.Cryptography;
using EZEHR.Models;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Spectre.Console;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Commands;

/// <summary>Where the tools point and what they hold, so a test run starts against the stack you meant.</summary>
public sealed class EnvCommand : AsyncCommand<NetworkSettings>
{
    public const string ClinicianObjectIdVariable = "EZEHR_CLINICIAN_OBJECT_ID";

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        NetworkSettings settings,
        CancellationToken cancellationToken
    )
    {
        Table table = new Table().HideHeaders().AddColumn("setting").AddColumn("value");
        table.AddRow(
            new Text("site"),
            new Text(
                $"{settings.BaseUrl} (from {settings.BaseUrlSource}): {await DescribeSiteAsync(settings.BaseUrl)}"
            )
        );
        table.AddRow(
            new Text("database"),
            new Text(
                ToolDatabase.IsConfigured
                    ? $"{ToolDatabase.Describe()} (from {AppOptions.ConnectionStringKey})"
                    : $"not set: {AppOptions.ConnectionStringKey} is empty, so patient, db and demo won't work. Run: source scripts/local.env (or scripts/azure.env)"
            )
        );
        string? clinicianObjectId = Environment.GetEnvironmentVariable(ClinicianObjectIdVariable);
        table.AddRow(
            new Text("clinician"),
            new Text(
                string.IsNullOrWhiteSpace(clinicianObjectId)
                    ? $"{ClinicianObjectIdVariable} not set (demo stage's launch step needs it)"
                    : $"{ClinicianObjectIdVariable} = {clinicianObjectId}"
            )
        );
        string pkiDirectory = ToolPaths.PkiDirectory;
        int certificateCount = Directory.Exists(pkiDirectory)
            ? Directory.GetFiles(pkiDirectory, "*.pfx").Length
            : 0;
        string pkiSource = string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(ToolPaths.PkiDirectoryVariable)
        )
            ? "the default"
            : ToolPaths.PkiDirectoryVariable;
        table.AddRow(
            new Text("certificates"),
            new Text($"{pkiDirectory} (from {pkiSource}): {certificateCount} .pfx files")
        );
        string platformPath = ToolPaths.Pki("platform.pfx");
        table.AddRow(
            new Text("platform pin"),
            new Text(
                File.Exists(platformPath)
                    ? PfxFile
                        .Load(platformPath)
                        .Certificate.GetCertHashString(HashAlgorithmName.SHA256)
                    : "no platform.pfx (ezehr-tools certs platform)"
            )
        );
        AnsiConsole.Console.Write(table);
        return 0;
    }

    /// <summary>"answers 200 OK", or why it didn't.</summary>
    private static async Task<string> DescribeSiteAsync(string baseUrl)
    {
        using HttpClient client = NetworkClient.Create(baseUrl, null);
        client.Timeout = TimeSpan.FromSeconds(3);
        try
        {
            using HttpResponseMessage response = await client.GetAsync("");
            return $"answers {(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (ToolException exception)
        {
            return $"no answer ({exception.Message})";
        }
        catch (TaskCanceledException)
        {
            return "no answer within 3 seconds";
        }
    }
}
