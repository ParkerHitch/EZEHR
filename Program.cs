using System.Text.Json.Serialization;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Server.Kestrel.Https;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
AppOptions appOptions = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>()
    ?? new AppOptions();

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Db>();

builder.Services.AddScoped<CallerManager>();
builder.Services.AddScoped<AuditManager>();
builder.Services.AddScoped<GrantManager>();
builder.Services.AddScoped<AccessManager>();
builder.Services.AddScoped<InviteManager>();
builder.Services.AddScoped<RecordManager>();

if (appOptions.KeyWrapper.Mode == "KeyVault")
{
    builder.Services.AddSingleton<IKeyWrapService, KeyVaultKeyWrapService>();
}
else if (appOptions.KeyWrapper.Mode == "Local")
{
    builder.Services.AddSingleton<IKeyWrapService, LocalKeyWrapService>();
}
else
{
    throw new InvalidOperationException(
        $"Unknown App:KeyWrapper:Mode '{appOptions.KeyWrapper.Mode}'."
    );
}

// The network door's client certificate. Only trust a forwarded header when a proxy that verifies mutual TLS sets it.
builder.Services.AddSingleton<IClientCertificateService, ConnectionClientCertificateService>();
if (!string.IsNullOrEmpty(appOptions.ClientCertificateHeader))
{
    builder.Services.AddCertificateForwarding(forwarding =>
        forwarding.CertificateHeader = appOptions.ClientCertificateHeader
    );
}
else
{
    builder.WebHost.ConfigureKestrel(kestrel =>
        kestrel.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            // Accepted at the TLS layer, validated by CertificateValidator.
            https.AllowAnyClientCertificate();
        })
    );
}

builder
    .Services.AddControllers()
    .AddJsonOptions(json =>
        json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter())
    );
builder.Services.AddRazorPages();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

if (!string.IsNullOrEmpty(appOptions.ClientCertificateHeader))
{
    app.UseCertificateForwarding();
}
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapControllers();

await app.Services.GetRequiredService<Db>().EnsureSchemaAsync();

await app.RunAsync();

/// <summary>Exposed so EZEHR.Tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
