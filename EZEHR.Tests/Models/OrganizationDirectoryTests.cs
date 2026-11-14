using EZEHR.Models;
using EZEHR.Services;

namespace EZEHR.Tests.Models;

public class OrganizationDirectoryTests
{
    private static readonly Guid RiversideTenant = Guid.Parse(
        "8f6c1c3e-2d4b-4b7a-9a51-1f0e2c7d9b10"
    );

    private static readonly Organization Riverside = new(
        "urn:provider:riverside",
        PartyKind.Provider,
        "Riverside Primary Care",
        "riverside.providers.ezehr",
        RiversideTenant
    );

    private static readonly Organization KidneyCheck = new(
        "urn:app:kidney-check",
        PartyKind.App,
        "Kidney Check",
        "kidney-check.apps.ezehr",
        null
    );

    private readonly TestPki _pki = new();
    private readonly OrganizationDirectory _directory = new([Riverside, KidneyCheck]);

    [Fact]
    public void FindByCertificate_matches_cn_case_insensitively()
    {
        Party? party = _directory.FindByCertificate(_pki.Issue("Riverside.Providers.EZEHR"));

        Assert.Equal("urn:provider:riverside", party?.SubjectUri);
    }

    [Fact]
    public void FindByCertificate_unknown_cn_is_null()
    {
        Assert.Null(_directory.FindByCertificate(_pki.Issue("mallory.providers.ezehr")));
    }

    [Fact]
    public void FindProviderByTenant_finds_only_the_mapped_provider()
    {
        Assert.Equal(
            "urn:provider:riverside",
            _directory.FindProviderByTenant(RiversideTenant)?.SubjectUri
        );
        Assert.Null(_directory.FindProviderByTenant(Guid.NewGuid()));
    }

    [Fact]
    public void FindByUri_round_trips()
    {
        Assert.Equal(KidneyCheck.ToParty(), _directory.FindByUri("urn:app:kidney-check"));
        Assert.Null(_directory.FindByUri("urn:app:nope"));
    }

    [Fact]
    public void Organizations_vouch_for_themselves()
    {
        foreach (Organization organization in OrgDirectory.DemoOrganizations)
        {
            Assert.Equal(organization.Uri, organization.ToParty().OrgUri);
        }
    }
}
