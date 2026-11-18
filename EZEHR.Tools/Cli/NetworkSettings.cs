using System.ComponentModel;
using EZEHR.Tools.Net;
using Spectre.Console.Cli;

namespace EZEHR.Tools.Cli;

/// <summary>Settings for every command that calls EZEHR: where the site is.</summary>
public class NetworkSettings : CommandSettings
{
    [CommandOption("--base-url <url>")]
    [Description("The EZEHR site to call. Default: EZEHR_BASE_URL, else https://localhost:7189")]
    public string? BaseUrlOption { get; init; }

    public string BaseUrl
    {
        get
        {
            return string.IsNullOrWhiteSpace(BaseUrlOption) ? NetworkClient.BaseUrl : BaseUrlOption;
        }
    }

    /// <summary>Where BaseUrl came from, for `env`.</summary>
    public string BaseUrlSource
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BaseUrlOption))
            {
                return "--base-url";
            }
            return string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(NetworkClient.BaseUrlVariable)
            )
                ? "the default"
                : NetworkClient.BaseUrlVariable;
        }
    }
}
