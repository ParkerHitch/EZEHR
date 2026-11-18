using System.Net;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Spectre.Console;

namespace EZEHR.Tools.Calls;

/// <summary>The platform operator's calls, with .pki/platform.pfx: admitting a provider or an app to the network.</summary>
public static class PlatformCalls
{
    /// <summary>Adds the provider to EZEHR; already there counts as done. Remembers it in .pki/organizations.json.</summary>
    public static async Task RegisterProviderAsync(string baseUrl, string slug, string displayName)
    {
        const string path = "api/network/register-provider";
        using HttpClient client = PlatformClient(baseUrl);
        NetworkLog.Request(
            "POST",
            path,
            $"{displayName}, certificate CN {OrgDirectory.ProviderCommonName(slug)}"
        );
        using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
            client,
            path,
            new RegisterProviderRequest(slug, displayName)
        );
        await ReportAsync(
            response,
            OrgDirectory.ProviderUri(slug),
            $"{displayName} is on the network as {OrgDirectory.ProviderUri(slug)}"
        );
        ToolOrganizations.Add(
            new Organization(
                OrgDirectory.ProviderUri(slug),
                PartyKind.Provider,
                displayName,
                OrgDirectory.ProviderCommonName(slug),
                null
            )
        );
    }

    /// <summary>Adds the app to EZEHR; already there counts as done. Patients then see it on their Access page, off.</summary>
    public static async Task RegisterAppAsync(
        string baseUrl,
        string slug,
        string displayName,
        GrantScope scope,
        string description
    )
    {
        const string path = "api/network/register-app";
        using HttpClient client = PlatformClient(baseUrl);
        NetworkLog.Request(
            "POST",
            path,
            $"{displayName}, certificate CN {OrgDirectory.AppCommonName(slug)}, scope {scope}"
        );
        using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
            client,
            path,
            new RegisterAppRequest(slug, displayName, scope, description)
        );
        await ReportAsync(
            response,
            OrgDirectory.AppUri(slug),
            $"{displayName} is on the network as {OrgDirectory.AppUri(slug)}; patients can turn it on"
        );
        ToolOrganizations.Add(
            new Organization(
                OrgDirectory.AppUri(slug),
                PartyKind.App,
                displayName,
                OrgDirectory.AppCommonName(slug),
                null,
                scope,
                description
            )
        );
    }

    private static HttpClient PlatformClient(string baseUrl)
    {
        return NetworkClient.Create(
            baseUrl,
            IdentityReference.Parse(IdentityReference.Platform).RequirePfx()
        );
    }

    /// <summary>201 and 409 (already there) are both done; anything else is a failure.</summary>
    private static async Task ReportAsync(
        HttpResponseMessage response,
        string organizationUri,
        string addedDetail
    )
    {
        if (response.StatusCode == HttpStatusCode.Created)
        {
            NetworkLog.Succeeded(response, addedDetail);
            return;
        }
        await NetworkLog.FailedAsync(response);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            AnsiConsole.Console.WriteLine($"  {organizationUri} was already on the network.");
            return;
        }
        throw new ToolException($"EZEHR didn't add {organizationUri}.");
    }
}
