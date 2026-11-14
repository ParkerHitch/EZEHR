using EZEHR.Access;
using EZEHR.Auth;
using EZEHR.Data;
using EZEHR.Domain;
using EZEHR.Records;
using EZEHR.Security.Crypto;
using EZEHR.Security.Pki;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace EZEHR;

/// <summary>Registers the server core: database, PKI, encryption, records, audit and the network door.</summary>
public static class CoreServices
{
    public static WebApplicationBuilder AddEzehrCore(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var options = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
        services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
        services.AddSingleton(TimeProvider.System);

        // One SQLite connection per request.
        services.AddScoped(sp => Db.Open(sp.GetRequiredService<IOptions<AppOptions>>().Value.DbPath));

        // PKI: trust anchors are the public EZCA root and issuing CA, committed under Security/Trust/.
        services.AddSingleton(sp => new CertificateValidator(
            CertificateValidator.LoadPem(options.TrustAnchorsPemPath),
            CertificateValidator.LoadPem(options.IntermediatesPemPath),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<OrgDirectory>();
        services.AddSingleton<RecordVerifier>();
        services.AddSingleton<CallerResolver>();

        // Envelope encryption.
        services.AddSingleton<IKeyWrapper>(sp => options.KeyWrapper.Mode switch
        {
            "KeyVault" => new KeyVaultKeyWrapper(new Uri(options.KeyVault.KeyId
                ?? throw new InvalidOperationException("App:KeyVault:KeyId is required when App:KeyWrapper:Mode is KeyVault."))),
            "Local" => new LocalKeyWrapper(LocalKek(options, builder.Environment)),
            var other => throw new InvalidOperationException($"Unknown App:KeyWrapper:Mode '{other}'."),
        });
        services.AddScoped(sp => new RecordStore(sp.GetRequiredService<Microsoft.Data.Sqlite.SqliteConnection>(),
            sp.GetRequiredService<IKeyWrapper>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<AuditLog>();
        services.AddScoped<InviteService>();
        services.AddScoped<GrantService>();
        services.AddScoped<AccessGate>();
        services.AddScoped<RecordReader>();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        // The network door's client certificate. Only trust a forwarded header when a proxy that verifies mutual TLS sets it.
        services.AddSingleton<IClientCertificateSource, ConnectionClientCertificateSource>();
        if (!string.IsNullOrEmpty(options.ClientCertificateHeader))
            services.AddCertificateForwarding(o => o.CertificateHeader = options.ClientCertificateHeader);
        else
            builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(https =>
            {
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                https.AllowAnyClientCertificate(); // accepted at the TLS layer, validated by CertificateValidator
            }));

        return builder;
    }

    public static WebApplication UseEzehrCore(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
        if (!string.IsNullOrEmpty(options.ClientCertificateHeader)) app.UseCertificateForwarding();
        return app;
    }

    private static byte[] LocalKek(AppOptions options, IHostEnvironment env)
    {
        if (!string.IsNullOrEmpty(options.LocalKekBase64)) return Convert.FromBase64String(options.LocalKekBase64);
        if (!env.IsDevelopment())
            throw new InvalidOperationException("App:LocalKekBase64 is required for App:KeyWrapper:Mode=Local outside Development. Use KeyVault in production.");

        // Development convenience: a random key kept in the git-ignored .pki folder, so the dev database survives restarts.
        var path = Path.Combine(env.ContentRootPath, ".pki", "dev-kek.bin");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var kek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, kek);
        return kek;
    }
}
