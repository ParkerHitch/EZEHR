using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;

namespace EZEHR.Tests.Api;

/// <summary>Stands in for the TLS layer in tests: reads the client certificate from a test-only header. Never registered by the app.</summary>
public sealed class TestHeaderCertificateService : IClientCertificateService
{
    public const string Header = "X-Test-Client-Cert";

    public Task<X509Certificate2?> GetAsync(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue(Header, out StringValues value))
        {
            return Task.FromResult<X509Certificate2?>(null);
        }
        X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(value.ToString())
        );
        return Task.FromResult<X509Certificate2?>(certificate);
    }
}

/// <summary>Hosts the real app against a test database, a local key wrapper, the test CA and a fake clock.</summary>
public sealed class EzehrFactory(TestDatabase database) : WebApplicationFactory<Program>
{
    private readonly string _trustDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ezehr-trust-{Guid.NewGuid():N}"
    );

    public TestPki Pki { get; } = new();

    public TestDatabase Database { get; } = database;

    public HttpClient Client(X509Certificate2? certificate = null)
    {
        HttpClient client = CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }
        );
        if (certificate is not null)
        {
            client.DefaultRequestHeaders.Add(
                TestHeaderCertificateService.Header,
                Convert.ToBase64String(certificate.RawData)
            );
        }
        return client;
    }

    /// <summary>Runs code against the app's own services, in a request-like scope.</summary>
    public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using IServiceScope scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_trustDirectory);
        string anchorsPath = Path.Combine(_trustDirectory, "anchors.pem");
        File.WriteAllText(anchorsPath, Pki.Ca.Root.ExportCertificatePem());

        builder.UseEnvironment("Testing");
        builder.UseSetting(AppOptions.ConnectionStringKey, Database.ConnectionString);
        builder.UseSetting("App:KeyWrapper:Mode", "Local");
        builder.UseSetting(
            "App:LocalKekBase64",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        );
        builder.UseSetting("App:Origin", "https://ezehr.test");
        builder.UseSetting("App:TrustAnchorsPemPath", anchorsPath);
        builder.UseSetting(
            "App:IntermediatesPemPath",
            Path.Combine(_trustDirectory, "intermediates.pem")
        );
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Pki.Clock);
            services.RemoveAll<IClientCertificateService>();
            services.AddSingleton<IClientCertificateService, TestHeaderCertificateService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_trustDirectory))
        {
            Directory.Delete(_trustDirectory, true);
        }
    }
}
