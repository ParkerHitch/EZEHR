using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Api;
using EZEHR.Tests.Auth;
using EZEHR.Tests.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EZEHR.Tests.Flows;

/// <summary>Sam downloads the whole record; each entry carries its organization's signature as FHIR Provenance.</summary>
public class ExportTests : IAsyncLifetime
{
    private EzehrFactory _app = null!;
    private HttpClient _phone = null!;
    private X509Certificate2 _lakeside = null!;
    private Guid _sam;

    public async Task InitializeAsync()
    {
        _app = new EzehrFactory(await SqlServer.CreateDatabaseAsync());
        _phone = _app.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            }
        );
        _lakeside = _app.Pki.Issue(OrgDirectory.ProviderCommonName("lakeside"));
        await EnrollSamAsync();
        _sam = (
            await _app.WithAsync(services =>
                services
                    .GetRequiredService<InviteManager>()
                    .FindLinkedPatientAsync(OrgDirectory.ProviderUri("lakeside"), "LUC-20417")
            )
        )!.Value;
        HttpResponseMessage pushed = await _app.Client(_lakeside)
            .PostAsJsonAsync(
                "/api/network/push-signed-records",
                new
                {
                    providerPatientId = "LUC-20417",
                    signedEntries = new[]
                    {
                        Signed(
                            new
                            {
                                resourceType = "Observation",
                                id = "egfr-1",
                                status = "final",
                            }
                        ),
                        Signed(new { resourceType = "Condition", id = "ckd-1" }),
                    },
                }
            );
        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _app.Database.DisposeAsync();
    }

    private string Signed(object resource)
    {
        return RecordSigner.Sign(
            JsonSerializer.SerializeToElement(resource),
            _lakeside,
            [],
            _app.Pki.Clock.GetUtcNow()
        );
    }

    private async Task EnrollSamAsync()
    {
        Party lakeside = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri("lakeside"))!;
        string inviteCode = await _app.WithAsync(async services =>
            (
                await services
                    .GetRequiredService<InviteManager>()
                    .CreateEnrollmentInviteAsync(new VerifiedCaller(lakeside, null), "LUC-20417")
            ).InviteCode!
        );
        JsonElement options = await (
            await _phone.PostAsJsonAsync(
                "/api/patient/passkey/create-registration-options",
                new { inviteCode, displayName = "Sam Taylor" }
            )
        ).Content.ReadFromJsonAsync<JsonElement>();
        string userId = options.GetProperty("user").GetProperty("id").GetString()!;
        _app.Fido2.NextRegistration = FakeFido2Service.NewPasskey(
            Convert.FromBase64String(
                userId
                    .Replace('-', '+')
                    .Replace('_', '/')
                    .PadRight((userId.Length + 3) / 4 * 4, '=')
            )
        );
        HttpResponseMessage completed = await _phone.PostAsJsonAsync(
            "/api/patient/passkey/complete-registration",
            new
            {
                attestation = new
                {
                    id = "AQID",
                    rawId = "AQID",
                    type = "public-key",
                    response = new
                    {
                        attestationObject = "AQID",
                        clientDataJSON = "AQID",
                        transports = Array.Empty<string>(),
                    },
                    clientExtensionResults = new { },
                },
                kidneyCheckOptIn = false,
            }
        );
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Fact]
    public async Task Export_bundle_has_provenance_per_entry_and_signatures_verify()
    {
        ExportPatientRecordResult export = await _app.WithAsync(services =>
            services.GetRequiredService<RecordManager>().ExportPatientRecordAsync(_sam)
        );

        JsonObject bundle = export.Bundle;
        Assert.Equal("Bundle", (string?)bundle["resourceType"]);
        Assert.Equal("collection", (string?)bundle["type"]);
        List<JsonObject> resources = bundle["entry"]!
            .AsArray()
            .Select(entry => entry!["resource"]!.AsObject())
            .ToList();
        List<JsonObject> provenances = resources
            .Where(resource => (string?)resource["resourceType"] == "Provenance")
            .ToList();
        List<JsonObject> records = resources.Except(provenances).ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(records.Count, provenances.Count);

        Dictionary<string, JsonObject> recordsByFullUrl = bundle["entry"]!
            .AsArray()
            .Where(entry => (string?)entry!["resource"]!["resourceType"] != "Provenance")
            .ToDictionary(
                entry => (string)entry!["fullUrl"]!,
                entry => entry!["resource"]!.AsObject()
            );
        foreach (JsonObject provenance in provenances)
        {
            JsonObject signature = provenance["signature"]![0]!.AsObject();
            Assert.Equal("application/jose", (string?)signature["sigFormat"]);
            Assert.Equal("urn:iso-astm:E1762-95:2013", (string?)signature["type"]![0]!["system"]);
            Assert.Equal("1.2.840.10065.1.12.1.1", (string?)signature["type"]![0]!["code"]);
            string jws = Encoding.UTF8.GetString(
                Convert.FromBase64String((string)signature["data"]!)
            );
            VerifiedEntry verified = RecordVerifier.Verify(
                Guid.Empty,
                jws,
                _app.Pki.TrustAnchors,
                OrgDirectory.Demo
            );

            Assert.Equal(
                $"{verified.Signer.DisplayName}, {verified.Signer.OrgName}",
                (string?)provenance["agent"]![0]!["who"]!["display"]
            );
            Assert.Equal(verified.SignedAt, DateTimeOffset.Parse((string)provenance["recorded"]!));
            JsonObject target = recordsByFullUrl[(string)provenance["target"]![0]!["reference"]!];
            Assert.Equal(verified.ResourceType, (string?)target["resourceType"]);
            Assert.Equal(verified.Resource.GetProperty("id").GetString(), (string?)target["id"]);
        }
    }

    [Fact]
    public async Task Export_downloads_for_the_signed_in_patient()
    {
        HttpResponseMessage response = await _phone.GetAsync("/Patient/Home?handler=Export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/fhir+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            "sam-taylor-record.json",
            response.Content.Headers.ContentDisposition!.FileName
        );
        JsonElement bundle = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("collection", bundle.GetProperty("type").GetString());
        Assert.Equal(4, bundle.GetProperty("entry").GetArrayLength());
    }

    [Fact]
    public async Task Home_links_to_the_export()
    {
        string home = await _phone.GetStringAsync("/Patient/Home");

        Assert.Contains("href=\"/Patient/Home?handler=Export\"", home);
        Assert.Contains("Export my record", home);
    }
}
