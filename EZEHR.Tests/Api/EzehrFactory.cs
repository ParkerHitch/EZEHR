using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Auth;
using EZEHR.Security.Pki;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EZEHR.Tests.Api;

/// <summary>Stands in for the TLS layer in tests: reads the client certificate from a test-only header. Never registered by the app.</summary>
public sealed class TestHeaderCertificateSource : IClientCertificateSource
{
    public const string Header = "X-Test-Client-Cert";

    public Task<X509Certificate2?> GetAsync(HttpContext ctx) =>
        Task.FromResult(ctx.Request.Headers.TryGetValue(Header, out var v)
            ? X509CertificateLoader.LoadCertificate(Convert.FromBase64String(v.ToString()))
            : null);
}

/// <summary>Hosts the real app against a temp database, a local key wrapper, the test CA and a fake clock.</summary>
public sealed class EzehrFactory : WebApplicationFactory<Program>
{
    public TestPki Pki { get; } = new();
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"ezehr-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DbPath", DbPath);
        builder.UseSetting("App:KeyWrapper:Mode", "Local");
        builder.UseSetting("App:LocalKekBase64", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        builder.UseSetting("App:Origin", "https://ezehr.test");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Pki.Clock);
            services.RemoveAll<CertificateValidator>();
            services.AddSingleton(Pki.Validator);
            services.RemoveAll<IClientCertificateSource>();
            services.AddSingleton<IClientCertificateSource, TestHeaderCertificateSource>();
        });
    }

    public HttpClient Client(X509Certificate2? cert = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (cert is not null) client.DefaultRequestHeaders.Add(TestHeaderCertificateSource.Header, Convert.ToBase64String(cert.RawData));
        return client;
    }

    public T With<T>(Func<IServiceProvider, T> f)
    {
        using var scope = Services.CreateScope();
        return f(scope.ServiceProvider);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }
}
