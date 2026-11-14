using System.Security.Cryptography;
using EZEHR.Data;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests;

/// <summary>The managers wired the way Program.cs wires them, over a test database and a fake clock.</summary>
public sealed class TestServices
{
    public TestServices(Db db, FakeTimeProvider clock, IKeyWrapService? keyWrapService = null)
    {
        IOptions<AppOptions> options = Options.Create(new AppOptions());
        Audit = new AuditManager(db, clock);
        Organizations = new OrganizationManager(db);
        Invites = new InviteManager(db, Organizations, clock, options);
        Grants = new GrantManager(db, Audit, Invites, Organizations, clock, options);
        Access = new AccessManager(Grants, Audit, clock);
        Records = new RecordManager(
            db,
            keyWrapService ?? LocalKeyWrapService(),
            Access,
            Audit,
            Invites,
            Organizations,
            clock,
            options,
            NullLogger<RecordManager>.Instance
        );
    }

    public AuditManager Audit { get; }

    public GrantManager Grants { get; }

    public AccessManager Access { get; }

    public InviteManager Invites { get; }

    public OrganizationManager Organizations { get; }

    public RecordManager Records { get; }

    public static LocalKeyWrapService LocalKeyWrapService()
    {
        AppOptions options = new()
        {
            LocalKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };
        return new LocalKeyWrapService(Options.Create(options), new HostingEnvironment());
    }
}
