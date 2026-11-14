using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using EZEHR.Controllers;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Identity.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
AppOptions appOptions = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>()
    ?? new AppOptions();

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.PostConfigure<AppOptions>(app =>
{
    // App Service rejects hyphens in setting names, so a tenant id is configured with underscores
    // (App__TrustedTenants__aaaa_bbbb_...). Turn them back into the hyphenated, lowercase id that Entra puts in `tid`.
    app.TrustedTenants = app.TrustedTenants.ToDictionary(
        tenant => tenant.Key.Replace('_', '-').ToLowerInvariant(),
        tenant => tenant.Value
    );
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Db>();

builder.Services.AddScoped<CallerManager>();
builder.Services.AddScoped<AuditManager>();
builder.Services.AddScoped<GrantManager>();
builder.Services.AddScoped<AccessManager>();
builder.Services.AddScoped<InviteManager>();
builder.Services.AddScoped<RecordManager>();
builder.Services.AddScoped<EnrollmentManager>();
builder.Services.AddScoped<ActivityManager>();

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

AuthenticationBuilder authentication = builder
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
        // A demo reset deletes the patient but not the phone's cookie. A cookie for a patient that no longer
        // exists is dropped, so the phone is signed out instead of acting for a ghost.
        cookie.Events.OnValidatePrincipal = async context =>
        {
            CallerManager callerManager =
                context.HttpContext.RequestServices.GetRequiredService<CallerManager>();
            RecordManager recordManager =
                context.HttpContext.RequestServices.GetRequiredService<RecordManager>();
            Guid? patientId = callerManager.PatientIdFrom(context.Principal);
            if (patientId is null || !await recordManager.PatientExistsAsync(patientId.Value))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CallerManager.PatientScheme);
            }
        };
    })
    // The clinician's shift: 10 hours from sign-in, never sliding. AccessManager enforces the same limit.
    // Whatever signs a clinician in (Entra below, or a passkey fallback) ends by issuing this cookie.
    .AddCookie(CallerManager.ClinicianScheme, cookie =>
    {
        cookie.Cookie.Name = "ezehr.clinician";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = AccessManager.ClinicianShift;
        cookie.SlidingExpiration = false;
        cookie.LoginPath = "/Clinician/Index";
        cookie.AccessDeniedPath = "/Clinician/Index";
    });

// Entra ID sign-in for clinicians, only once the app registration is configured. Sign-in only: an ID token, no
// client secret, no API tokens.
if (!string.IsNullOrEmpty(builder.Configuration["AzureAd:ClientId"]))
{
    authentication.AddMicrosoftIdentityWebApp(
        builder.Configuration.GetSection("AzureAd"),
        CallerManager.ClinicianOidcScheme,
        cookieScheme: null
    );
    builder.Services.PostConfigure<OpenIdConnectOptions>(CallerManager.ClinicianOidcScheme, oidc =>
    {
        oidc.SignInScheme = CallerManager.ClinicianScheme;
        oidc.ResponseType = "id_token";
        oidc.UseTokenLifetime = false;
        // Every sign-in makes the clinician authenticate again, even with a live Entra session, so signing out
        // and the end of a shift really end access, and the security-key tap happens each time.
        oidc.Prompt = "login";
        Func<TokenValidatedContext, Task> previous = oidc.Events.OnTokenValidated;
        oidc.Events.OnTokenValidated = async context =>
        {
            await previous(context);
            CallerManager callerManager =
                context.HttpContext.RequestServices.GetRequiredService<CallerManager>();
            context.Principal?.Identities.First().AddClaim(callerManager.SessionStartedClaim());
        };
    });
}

builder.Services.AddAuthorization(authorization =>
{
    authorization.AddPolicy(CallerManager.PatientScheme, policy =>
        policy
            .AddAuthenticationSchemes(CallerManager.PatientScheme)
            .RequireClaim(CallerManager.PatientIdClaim)
    );
    authorization.AddPolicy(CallerManager.ClinicianScheme, policy =>
        policy.AddAuthenticationSchemes(CallerManager.ClinicianScheme).RequireAuthenticatedUser()
    );
});

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

// The network door is open to anyone with or without a certificate, and every read attempt on a real patient is
// audited, so each client address gets a fixed number of calls a minute. A certificate can't be the key: anyone
// can make a new self-signed one per request.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(NetworkController.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
            }
        )
    );
});

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
    pages.Conventions.AuthorizeFolder("/Clinician", CallerManager.ClinicianScheme);
    pages.Conventions.AllowAnonymousToPage("/Clinician/Index");
    pages.Conventions.AllowAnonymousToPage("/Clinician/AccessEnded");
});

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Scripts only from this site and the one htmx file (its integrity hash is in _Layout); no inline script. The
// only cross-origin styles are that Bootstrap file (also hashed) and Google Fonts. The share QR code is a data: image.
app.Use(
    async (context, next) =>
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' https://unpkg.com/htmx.org@2.0.4/dist/htmx.min.js; "
            + "style-src 'self' https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css "
            + "https://fonts.googleapis.com; "
            + "font-src https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; "
            + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await next();
    }
);

if (!string.IsNullOrEmpty(appOptions.ClientCertificateHeader))
{
    app.UseCertificateForwarding();
}
app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
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
