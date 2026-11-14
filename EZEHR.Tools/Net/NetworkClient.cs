using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EZEHR.Tools.Net;

/// <summary>
/// An HttpClient for the network API (/api/network/*), sending an organization's certificate over mutual TLS.
/// App Service asks for the client certificate by TLS renegotiation, which needs TLS 1.2 and HTTP/1.1, and which
/// fails for bodies over 100 KB unless the client waits for "100 Continue" first.
/// </summary>
public static class NetworkClient
{
    public const string BaseUrlVariable = "EZEHR_BASE_URL";

    public const string DefaultBaseUrl = "https://localhost:7189";

    /// <summary>Matches the server: camelCase, enums as names.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>EZEHR_BASE_URL, or the local https profile from launchSettings.json.</summary>
    public static string BaseUrl
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(BaseUrlVariable);
            return string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured;
        }
    }

    /// <summary>A client that presents the certificate in pfxPath, or no certificate when pfxPath is null.</summary>
    public static HttpClient Create(string baseUrl, string? pfxPath)
    {
        Uri baseAddress = new(baseUrl.TrimEnd('/') + "/");
        SocketsHttpHandler handler = new();
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12;
        if (pfxPath is not null)
        {
            X509Certificate2 certificate = PfxFile.Load(pfxPath).Certificate;
            handler.SslOptions.ClientCertificates = [certificate];
            // Always offer our certificate, even when the server names no acceptable issuers.
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }
        if (baseAddress.IsLoopback)
        {
            // The local ASP.NET Core development certificate is often untrusted on Linux.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        HttpClient client = new(handler)
        {
            BaseAddress = baseAddress,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        client.DefaultRequestHeaders.ExpectContinue = true;
        return client;
    }

    /// <summary>POSTs a JSON body with a Content-Length (not chunked), so "Expect: 100-continue" applies.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        object body
    )
    {
        StringContent content = new(
            JsonSerializer.Serialize(body, Json),
            Encoding.UTF8,
            "application/json"
        );
        return client.PostAsync(path, content);
    }

    /// <summary>"401 Unauthorized {body}", for failures the tools print as-is.</summary>
    public static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        return $"{(int)response.StatusCode} {response.ReasonPhrase} {body}".TrimEnd();
    }
}
