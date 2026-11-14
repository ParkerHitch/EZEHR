using System.Security.Claims;
using System.Text.Encodings.Web;
using EZEHR.Managers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace EZEHR.Tests.Auth;

/// <summary>
/// Stands in for the clinician cookie in tests: the X-Test-Clinician header ("{role}|{session start, Unix
/// seconds}") signs Dr. Rivera of Riverside's Entra tenant in, with the claims Entra's ID token carries.
/// </summary>
public sealed class TestClinicianAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Header = "X-Test-Clinician";

    public const string RiversideTenant = "8f6c1c3e-2d4b-4b7a-9a51-1f0e2c7d9b10";

    public const string RiveraObjectId = "c2a7e5d1-9b3f-4e8a-a6c4-3d2b1e0f9a87";

    /// <summary>The app hosted with this handler in place of the clinician cookie, and Riverside's tenant trusted.</summary>
    public static WebApplicationFactory<Program> Install(WebApplicationFactory<Program> app)
    {
        return app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"App:TrustedTenants:{RiversideTenant}", "Riverside Primary Care");
            builder.ConfigureTestServices(services =>
                services.Configure<AuthenticationOptions>(authentication =>
                    authentication.SchemeMap[CallerManager.ClinicianScheme].HandlerType =
                        typeof(TestClinicianAuthHandler)
                )
            );
        });
    }

    public static string HeaderValue(string role, DateTimeOffset sessionStartedAt)
    {
        return $"{role}|{sessionStartedAt.ToUnixTimeSeconds()}";
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out StringValues value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        string[] parts = value.ToString().Split('|');
        ClaimsIdentity identity = new(
            [
                new Claim("http://schemas.microsoft.com/identity/claims/tenantid", RiversideTenant),
                new Claim(
                    "http://schemas.microsoft.com/identity/claims/objectidentifier",
                    RiveraObjectId
                ),
                new Claim("name", "Dr. Rivera"),
                new Claim("roles", parts[0]),
                new Claim(CallerManager.SessionStartedAtClaim, parts[1]),
            ],
            Scheme.Name
        );
        AuthenticationTicket ticket = new(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
