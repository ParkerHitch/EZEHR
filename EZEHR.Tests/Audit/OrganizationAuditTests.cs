using Dapper;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Audit;

/// <summary>A provider audits what its own clinicians and its own system did, and nothing else.</summary>
public class OrganizationAuditTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);

    private static readonly Party Riverside = OrgDirectory.Demo.FindByUri(
        "urn:provider:riverside"
    )!;

    private static readonly Party Lakeside = OrgDirectory.Demo.FindByUri("urn:provider:lakeside")!;

    private static readonly Party KidneyCheck = OrgDirectory.Demo.FindByUri(
        "urn:app:kidney-check"
    )!;

    private static readonly Party Rivera = new(
        PartyKind.Clinician,
        "urn:entra:tid-r:rivera",
        "Dr. Rivera",
        "Riverside Primary Care",
        "urn:provider:riverside"
    );

    private static readonly Party Chen = new(
        PartyKind.Clinician,
        "urn:entra:tid-r:chen",
        "Dr. Chen",
        "Riverside Primary Care",
        "urn:provider:riverside"
    );

    private static readonly Party Okafor = new(
        PartyKind.Clinician,
        "urn:entra:tid-l:okafor",
        "Dr. Okafor",
        "Lakeside Urgent Care",
        "urn:provider:lakeside"
    );

    private static readonly OrganizationAuditFilter All = new(null, null, null, null, null, 0, 100);

    private readonly FakeTimeProvider _clock = new(Start);
    private Guid _sam;
    private Guid _alex;
    private TestDatabase _database = null!;
    private TestServices _services = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
        _services = new TestServices(_database.Db, _clock);
        // The audit only logs attempts on real patients.
        _sam = await _services.Records.CreatePatientAsync("Sam Taylor");
        _alex = await _services.Records.CreatePatientAsync("Alex Kim");
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private static Caller Organization(Party organization)
    {
        return new VerifiedCaller(organization, null);
    }

    private async Task<AuditEvent> LogAsync(
        Party? actor,
        Guid patientId,
        string action = "ReadFullRecord",
        bool granted = true,
        DenyReason? reason = null
    )
    {
        return (
            await _services.Audit.AppendAsync(patientId, actor, action, null, granted, reason)
        )!;
    }

    private async Task LinkAsync(Guid patientId, Party provider, string providerPatientId)
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id)
            VALUES (@patientId, @providerUri, @providerPatientId)
            """,
            new
            {
                patientId,
                providerUri = provider.SubjectUri,
                providerPatientId,
            }
        );
    }

    private Task<ReadOrganizationAuditResult> ReadAsync(
        Caller caller,
        OrganizationAuditFilter filter
    )
    {
        return _services.OrganizationAudit.ReadOrganizationAuditAsync(caller, filter);
    }

    private static List<long> Ids(ReadOrganizationAuditResult result)
    {
        return result.Events.Select(auditEvent => auditEvent.Id).ToList();
    }

    [Fact]
    public async Task Provider_sees_only_its_own_clinicians_and_systems()
    {
        AuditEvent rivera = await LogAsync(Rivera, _sam);
        AuditEvent okafor = await LogAsync(Okafor, _sam);
        AuditEvent push = await LogAsync(Lakeside, _sam, "PushSignedRecords");
        await LogAsync(null, _sam, granted: false, reason: DenyReason.NoCertificate);
        AuditEvent kidney = await LogAsync(KidneyCheck, _sam, "ReadEgfrLabs");

        ReadOrganizationAuditResult riverside = await ReadAsync(Organization(Riverside), All);
        ReadOrganizationAuditResult lakeside = await ReadAsync(
            Organization(Lakeside),
            All with
            {
                PatientId = _sam,
            }
        );
        ReadOrganizationAuditResult kidneyCheck = await ReadAsync(Organization(KidneyCheck), All);

        Assert.True(riverside.Allowed);
        Assert.Equal([rivera.Id], Ids(riverside));
        Assert.Equal(PartyKind.Clinician, riverside.Events[0].ActorKind);
        Assert.Equal(Rivera.SubjectUri, riverside.Events[0].ActorUri);
        Assert.Equal("Dr. Rivera", riverside.Events[0].ActorName);
        Assert.Equal(_sam, riverside.Events[0].PatientId);
        Assert.Equal([okafor.Id, push.Id], Ids(lakeside));
        Assert.Equal([kidney.Id], Ids(kidneyCheck));
    }

    [Fact]
    public async Task Denied_attempts_by_own_clinician_are_included()
    {
        await LogAsync(Rivera, _sam, granted: false, reason: DenyReason.GrantRevoked);

        ReadOrganizationAuditResult result = await ReadAsync(Organization(Riverside), All);

        OrganizationAuditEvent refused = Assert.Single(result.Events);
        Assert.False(refused.Granted);
        Assert.Equal(DenyReason.GrantRevoked, refused.Reason);
    }

    [Fact]
    public async Task Filters_by_clinician_uri()
    {
        await LogAsync(Rivera, _sam);
        AuditEvent chen = await LogAsync(Chen, _sam);
        await LogAsync(Okafor, _sam);

        ReadOrganizationAuditResult onlyChen = await ReadAsync(
            Organization(Riverside),
            All with
            {
                ClinicianUri = Chen.SubjectUri,
            }
        );
        ReadOrganizationAuditResult someoneElsesClinician = await ReadAsync(
            Organization(Riverside),
            All with
            {
                ClinicianUri = Okafor.SubjectUri,
            }
        );

        Assert.Equal([chen.Id], Ids(onlyChen));
        Assert.Empty(someoneElsesClinician.Events);
    }

    [Fact]
    public async Task Filters_by_patient_and_reports_providers_own_patient_id()
    {
        await LinkAsync(_sam, Riverside, "RPC-1107");
        await LinkAsync(_sam, Lakeside, "LUC-20417");
        AuditEvent sam = await LogAsync(Rivera, _sam);
        AuditEvent alex = await LogAsync(Rivera, _alex);

        ReadOrganizationAuditResult byOwnId = await ReadAsync(
            Organization(Riverside),
            All with
            {
                ProviderPatientId = "RPC-1107",
            }
        );
        ReadOrganizationAuditResult all = await ReadAsync(Organization(Riverside), All);
        ReadOrganizationAuditResult unknownId = await ReadAsync(
            Organization(Riverside),
            All with
            {
                ProviderPatientId = "RPC-0000",
            }
        );
        ReadOrganizationAuditResult othersId = await ReadAsync(
            Organization(Riverside),
            All with
            {
                ProviderPatientId = "LUC-20417",
            }
        );

        Assert.Equal([sam.Id], Ids(byOwnId));
        Assert.Equal("RPC-1107", byOwnId.Events[0].ProviderPatientId);
        Assert.Equal([sam.Id, alex.Id], Ids(all));
        Assert.Null(all.Events[1].ProviderPatientId);
        Assert.Empty(unknownId.Events);
        Assert.Empty(othersId.Events);
    }

    [Fact]
    public async Task Patient_with_two_ids_at_one_provider_appears_once()
    {
        await LinkAsync(_sam, Riverside, "RPC-2208");
        await LinkAsync(_sam, Riverside, "RPC-1107");
        await LogAsync(Rivera, _sam);

        ReadOrganizationAuditResult result = await ReadAsync(Organization(Riverside), All);

        OrganizationAuditEvent only = Assert.Single(result.Events);
        Assert.Equal("RPC-1107", only.ProviderPatientId);
    }

    [Fact]
    public async Task Since_is_inclusive_and_until_exclusive()
    {
        await LogAsync(Rivera, _sam);
        _clock.Advance(TimeSpan.FromHours(1));
        AuditEvent middle = await LogAsync(Rivera, _sam);
        _clock.Advance(TimeSpan.FromHours(1));
        await LogAsync(Rivera, _sam);

        ReadOrganizationAuditResult result = await ReadAsync(
            Organization(Riverside),
            All with
            {
                Since = Start.AddHours(1),
                Until = Start.AddHours(2),
            }
        );

        Assert.Equal([middle.Id], Ids(result));
    }

    [Fact]
    public async Task Pages_by_after_id()
    {
        AuditEvent first = await LogAsync(Rivera, _sam);
        AuditEvent second = await LogAsync(Rivera, _sam);
        AuditEvent third = await LogAsync(Rivera, _sam);

        ReadOrganizationAuditResult page1 = await ReadAsync(
            Organization(Riverside),
            All with
            {
                Limit = 2,
            }
        );
        ReadOrganizationAuditResult page2 = await ReadAsync(
            Organization(Riverside),
            All with
            {
                Limit = 2,
                AfterId = page1.NextAfterId!.Value,
            }
        );
        ReadOrganizationAuditResult exactlyFull = await ReadAsync(
            Organization(Riverside),
            All with
            {
                Limit = 3,
            }
        );

        Assert.Equal([first.Id, second.Id], Ids(page1));
        Assert.Equal(second.Id, page1.NextAfterId);
        Assert.Equal([third.Id], Ids(page2));
        Assert.Null(page2.NextAfterId);
        Assert.Equal([first.Id, second.Id, third.Id], Ids(exactlyFull));
        Assert.Null(exactlyFull.NextAfterId);
    }

    [Fact]
    public async Task Unverified_caller_is_refused()
    {
        await LogAsync(Rivera, _sam);

        ReadOrganizationAuditResult result = await ReadAsync(
            new UnverifiedCaller(DenyReason.NoCertificate),
            All
        );

        Assert.False(result.Allowed);
        Assert.Equal(DenyReason.NoCertificate, result.Reason);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Clinician_caller_is_refused()
    {
        await LogAsync(Rivera, _sam);

        ReadOrganizationAuditResult result = await ReadAsync(
            new VerifiedCaller(Rivera, Start),
            All
        );

        Assert.False(result.Allowed);
        Assert.Equal(DenyReason.WrongPartyKind, result.Reason);
        Assert.Empty(result.Events);
    }
}
