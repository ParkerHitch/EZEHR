using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Api;

/// <summary>The platform operator adds a provider over the network door; nobody else can.</summary>
public class RegisterProviderApiTests : IAsyncLifetime
{
    private EzehrFactory _app = null!;
    private X509Certificate2 _harbor = null!;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _harbor = _app.Pki.Issue(OrgDirectory.ProviderCommonName("harbor"));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private Task<HttpResponseMessage> RegisterAsync(
        X509Certificate2? certificate,
        string slug = "harbor",
        string displayName = "Harbor Clinic"
    )
    {
        return _app.Client(certificate)
            .PostAsJsonAsync(
                "/api/network/register-provider",
                new RegisterProviderRequest(slug, displayName)
            );
    }

    private Task<HttpResponseMessage> InviteAsync(X509Certificate2 certificate)
    {
        return _app.Client(certificate)
            .PostAsJsonAsync(
                "/api/network/create-enrollment-invite",
                new CreateEnrollmentInviteRequest("HBR-001")
            );
    }

    [Fact]
    public async Task Platform_registers_provider_whose_certificate_then_works()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await InviteAsync(_harbor)).StatusCode);

        HttpResponseMessage response = await RegisterAsync(_app.Platform);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        RegisterProviderResponse body = (
            await response.Content.ReadFromJsonAsync<RegisterProviderResponse>()
        )!;
        Assert.Equal("urn:provider:harbor", body.Uri);
        Assert.Equal("harbor.providers.ezehr", body.CertificateCommonName);
        OrganizationDirectory directory = await _app.WithAsync(services =>
            services.GetRequiredService<OrganizationManager>().LoadDirectoryAsync()
        );
        Party harbor = directory.FindByCertificate(_harbor)!;
        Assert.Equal(PartyKind.Provider, harbor.Kind);
        Assert.Equal("Harbor Clinic", harbor.DisplayName);
        Assert.Null(directory.FindProviderByTenant(Guid.Empty));

        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(_harbor)).StatusCode);
    }

    [Fact]
    public async Task Existing_provider_is_409_and_unchanged()
    {
        HttpResponseMessage response = await RegisterAsync(
            _app.Platform,
            "maple",
            "Someone Else"
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        OrganizationDirectory directory = await _app.WithAsync(services =>
            services.GetRequiredService<OrganizationManager>().LoadDirectoryAsync()
        );
        Assert.Equal(
            "Maple Street Family Medicine",
            directory.FindByUri("urn:provider:maple")!.DisplayName
        );
    }

    [Fact]
    public async Task Provider_certificate_cannot_register()
    {
        X509Certificate2 maple = _app.Pki.Issue(OrgDirectory.ProviderCommonName("maple"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(maple)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await InviteAsync(_harbor)).StatusCode);
    }

    [Fact]
    public async Task No_certificate_cannot_register()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(null)).StatusCode);
    }

    [Fact]
    public async Task Self_signed_certificate_with_pinned_thumbprint_cannot_register()
    {
        X509Certificate2 selfSigned = TestPki.SelfSigned(
            OrgDirectory.PlatformCommonName,
            _app.Pki.Clock.GetUtcNow()
        );
        _app.PlatformThumbprint = selfSigned.GetCertHashString(HashAlgorithmName.SHA256);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(selfSigned)).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_register_when_no_thumbprint_is_pinned()
    {
        _app.PlatformThumbprint = null;

        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(_app.Platform)).StatusCode);
    }

    [Theory]
    [InlineData("Harbor")]
    [InlineData("harbor clinic")]
    [InlineData("-harbor")]
    [InlineData("harbor.providers")]
    public async Task Slug_must_be_one_lowercase_label(string slug)
    {
        HttpResponseMessage response = await RegisterAsync(_app.Platform, slug);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
