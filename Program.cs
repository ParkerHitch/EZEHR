using System.Text.Json.Serialization;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
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
builder.Services.AddScoped<EnrollmentManager>();

// Patient passkeys. The relying party is the site's public origin, so passkeys only work on the real site.
Uri origin = new(appOptions.Origin);
builder.Services.AddFido2(fido2 =>
{
    fido2.ServerDomain = origin.Host;
    fido2.ServerName = "EZEHR";
    fido2.Origins = new HashSet<string> { origin.GetLeftPart(UriPartial.Authority) };
});
// Scoped, because AddFido2 registers IFido2 as scoped.
builder.Services.AddScoped<IFido2Service, Fido2Service>();

// Holds a passkey ceremony's challenge between its two requests.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(session =>
{
    session.IdleTimeout = TimeSpan.FromMinutes(5);
    session.Cookie.Name = "ezehr.passkey";
    session.Cookie.HttpOnly = true;
    session.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    session.Cookie.SameSite = SameSiteMode.Strict;
    session.Cookie.IsEssential = true;
});

builder
    .Services.AddAuthentication()
    .AddCookie(CallerManager.PatientScheme, cookie =>
    {
        cookie.Cookie.Name = "ezehr.patient";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(12);
        cookie.SlidingExpiration = false;
        cookie.LoginPath = "/Patient/SignIn";
        cookie.AccessDeniedPath = "/Patient/SignIn";
    });
builder.Services.AddAuthorization(authorization =>
    authorization.AddPolicy(CallerManager.PatientScheme, policy =>
        policy
            .AddAuthenticationSchemes(CallerManager.PatientScheme)
            .RequireClaim(CallerManager.PatientIdClaim)
    )
);

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
builder.Services.AddSingleton<IClientCertificateService, TlsClientCertificateService>();
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
builder.Services.AddRazorPages(pages =>
{
    pages.Conventions.AuthorizeFolder("/Patient", CallerManager.PatientScheme);
    pages.Conventions.AllowAnonymousToPage("/Patient/Enroll");
    pages.Conventions.AllowAnonymousToPage("/Patient/SignIn");
});

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
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapControllers();

await app.Services.GetRequiredService<Db>().EnsureSchemaAsync();

await app.RunAsync();

/// <summary>Exposed so EZEHR.Tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
