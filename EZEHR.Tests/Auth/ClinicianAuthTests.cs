using System.Security.Claims;
using EZEHR.Managers;
using EZEHR.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Auth;

/// <summary>CallerManager.EvaluateClinician on principals shaped like the ones Entra's ID token produces.</summary>
public class ClinicianAuthTests
{
    private const string RiversideTenant = "8f6c1c3e-2d4b-4b7a-9a51-1f0e2c7d9b10";
    private const string OtherTenant = "0b9f3a77-5e21-4c3d-8e6f-7a1b2c3d4e5f";
    private const string RiveraObjectId = "c2a7e5d1-9b3f-4e8a-a6c4-3d2b1e0f9a87";

    private readonly FakeTimeProvider _clock = new(TestPki.Start);
    private readonly CallerManager _callerManager;

    public ClinicianAuthTests()
    {
        AppOptions options = new()
        {
            TrustedTenants = { [RiversideTenant] = "Riverside Primary Care" },
        };
        _callerManager = new CallerManager(_clock, Options.Create(options));
    }

    private static ClaimsPrincipal Principal(
        string tenantId,
        string? role,
        DateTimeOffset? sessionStartedAt = null
    )
    {
        List<Claim> claims =
        [
            new("http://schemas.microsoft.com/identity/claims/tenantid", tenantId),
            new("http://schemas.microsoft.com/identity/claims/objectidentifier", RiveraObjectId),
            new("name", "Dr. Rivera"),
        ];
        if (role is not null)
        {
            claims.Add(new Claim("roles", role));
        }
        if (sessionStartedAt is DateTimeOffset started)
        {
            claims.Add(
                new Claim(
                    CallerManager.SessionStartedAtClaim,
                    started.ToUnixTimeSeconds().ToString()
                )
            );
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CallerManager.ClinicianScheme));
    }

    [Fact]
    public void Valid_clinician()
    {
        ClinicianCheck check = _callerManager.EvaluateClinician(
            Principal(RiversideTenant, CallerManager.ClinicianRole)
        );

        Assert.True(check.Ok);
        Assert.Null(check.ErrorCopy);
        Assert.Equal(
            new Party(
                PartyKind.Clinician,
                $"urn:entra:{RiversideTenant}:{RiveraObjectId}",
                "Dr. Rivera",
                "Riverside Primary Care"
            ),
            check.Party
        );
    }

    [Fact]
    public void Missing_role_returns_NotAClinician()
    {
        ClinicianCheck check = _callerManager.EvaluateClinician(
            Principal(RiversideTenant, "Reader")
        );

        Assert.False(check.Ok);
        Assert.Null(check.Party);
        Assert.Equal(Copy.NotAClinician, check.ErrorCopy);
    }

    [Fact]
    public void Untrusted_tenant_returns_UntrustedOrg()
    {
        ClinicianCheck check = _callerManager.EvaluateClinician(
            Principal(OtherTenant, CallerManager.ClinicianRole)
        );

        Assert.False(check.Ok);
        Assert.Null(check.Party);
        Assert.Equal(Copy.UntrustedOrg, check.ErrorCopy);
    }

    [Fact]
    public void Session_started_at_parsed()
    {
        DateTimeOffset signedInAt = TestPki.Start.AddMinutes(-30);

        ClinicianCheck check = _callerManager.EvaluateClinician(
            Principal(RiversideTenant, CallerManager.ClinicianRole, signedInAt)
        );
        Caller caller = _callerManager.FromClinician(
            Principal(RiversideTenant, CallerManager.ClinicianRole, signedInAt)
        );

        Assert.Equal(signedInAt, check.SessionStartedAt);
        VerifiedCaller verified = Assert.IsType<VerifiedCaller>(caller);
        Assert.Equal(signedInAt, verified.SessionStartedAt);
        Assert.Equal(check.Party, verified.Party);
    }
}
