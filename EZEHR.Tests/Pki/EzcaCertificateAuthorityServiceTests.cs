using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EZCAClient.Managers;
using EZCAClient.Models;
using EZEHR.Models;
using EZEHR.Services;

namespace EZEHR.Tests.Pki;

public class EzcaCertificateAuthorityServiceTests
{
    private static readonly AvailableCAModel Ca = new()
    {
        CAID = "ca-123",
        TemplateID = "template-456",
        CAFriendlyName = "EZEHR Issuing CA",
    };

    private readonly TestPki _pki = new();

    [Fact]
    public async Task Ezca_passes_subject_validity_and_csr()
    {
        FakeEzcaClient client = new(SuccessfulResponse());
        EzcaCertificateAuthorityService service = new(client, Ca);
        string csrPem = CreateCsrPem();

        await service.IssueAsync(csrPem, "lakeside.providers.ezehr", 365, CancellationToken.None);

        FakeEzcaClient.Request request = Assert.Single(client.Requests);
        Assert.Same(Ca, request.Ca);
        Assert.Equal(csrPem, request.Csr);
        Assert.Equal("lakeside.providers.ezehr", request.SubjectName);
        Assert.Equal(365, request.ValidityDays);
        SubjectAltValue subjectAltName = Assert.Single(request.SubjectAltNames);
        Assert.Equal("lakeside.providers.ezehr", subjectAltName.ValueSTR);
        Assert.Equal([CertificateValidator.ClientAuthenticationOid], request.Ekus);
    }

    [Fact]
    public async Task Ezca_maps_chain()
    {
        X509Certificate2 leaf = _pki.Issue("lakeside.providers.ezehr");
        X509Certificate2 issuing = LocalCertificateAuthorityService.CreateRoot(
            _pki.Clock,
            "EZEHR Issuing CA"
        );
        X509Certificate2 root = _pki.Ca.Root;
        FakeEzcaClient client = new(
            new CertificateCreatedResponse(
                leaf.ExportCertificatePem(),
                issuing.ExportCertificatePem(),
                // EZCA's PEM fields may come without headers; both forms must load.
                Convert.ToBase64String(root.RawData)
            )
        );
        EzcaCertificateAuthorityService service = new(client, Ca);

        IssuedCertificate issued = await service.IssueAsync(
            CreateCsrPem(),
            "lakeside.providers.ezehr",
            365,
            CancellationToken.None
        );

        Assert.Equal(leaf.Thumbprint, issued.Leaf.Thumbprint);
        Assert.False(issued.Leaf.HasPrivateKey);
        Assert.Equal(
            [issuing.Thumbprint, root.Thumbprint],
            issued.Chain.Select(certificate => certificate.Thumbprint)
        );
    }

    [Fact]
    public async Task Ezca_error_throws_with_message()
    {
        FakeEzcaClient client = new(
            new HttpRequestException("Subject name is not registered for this CA")
        );
        EzcaCertificateAuthorityService service = new(client, Ca);

        CertificateAuthorityException exception =
            await Assert.ThrowsAsync<CertificateAuthorityException>(() =>
                service.IssueAsync(
                    CreateCsrPem(),
                    "mallory.providers.ezehr",
                    365,
                    CancellationToken.None
                )
            );

        Assert.Contains("mallory.providers.ezehr", exception.Message);
        Assert.Contains("Subject name is not registered for this CA", exception.Message);
    }

    [Fact]
    public async Task Ezca_registers_missing_domain_with_known_owner()
    {
        AADObjectModel admin = new("oid-1", "EZCA Admin") { ObjectType = "User" };
        FakeEzcaClient client = new(SuccessfulResponse())
        {
            Domains =
            [
                new DomainInformationModel
                {
                    CAID = Ca.CAID,
                    Domain = "lakeside.providers.ezehr",
                    DomainOwners = [admin],
                },
            ],
        };
        EzcaCertificateAuthorityService service = new(client, Ca);

        bool registered = await service.EnsureDomainRegisteredAsync(
            "harbor.providers.ezehr",
            "oid-1",
            "ezca-admin"
        );

        Assert.True(registered);
        FakeEzcaClient.DomainRegistration registration = Assert.Single(client.Registrations);
        Assert.Same(Ca, registration.Ca);
        Assert.Equal("harbor.providers.ezehr", registration.Domain);
        Assert.Same(admin, Assert.Single(registration.Owners));
        Assert.Same(admin, Assert.Single(registration.Administrators));
    }

    [Fact]
    public async Task Ezca_skips_registered_domain()
    {
        FakeEzcaClient client = new(SuccessfulResponse())
        {
            Domains =
            [
                new DomainInformationModel { CAID = Ca.CAID, Domain = "Harbor.providers.ezehr" },
            ],
        };
        EzcaCertificateAuthorityService service = new(client, Ca);

        bool registered = await service.EnsureDomainRegisteredAsync(
            "harbor.providers.ezehr",
            "oid-1",
            "ezca-admin"
        );

        Assert.False(registered);
        Assert.Empty(client.Registrations);
    }

    [Fact]
    public async Task Ezca_domain_refusal_throws_with_message()
    {
        FakeEzcaClient client = new(SuccessfulResponse())
        {
            RegistrationResult = new APIResultModel(false, "Not a CA administrator"),
        };
        EzcaCertificateAuthorityService service = new(client, Ca);

        CertificateAuthorityException exception =
            await Assert.ThrowsAsync<CertificateAuthorityException>(() =>
                service.EnsureDomainRegisteredAsync("harbor.providers.ezehr", "oid-1", "admin")
            );

        Assert.Contains("harbor.providers.ezehr", exception.Message);
        Assert.Contains("Not a CA administrator", exception.Message);
        // A new account is sent as itself when EZCA doesn't list it anywhere yet.
        AADObjectModel owner = Assert.Single(Assert.Single(client.Registrations).Owners);
        Assert.Equal("oid-1", owner.ObjectId);
    }

    private CertificateCreatedResponse SuccessfulResponse()
    {
        X509Certificate2 leaf = _pki.Issue("lakeside.providers.ezehr");
        return new CertificateCreatedResponse(
            leaf.ExportCertificatePem(),
            _pki.Ca.Root.ExportCertificatePem(),
            _pki.Ca.Root.ExportCertificatePem()
        );
    }

    private static string CreateCsrPem()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest(
            "CN=lakeside.providers.ezehr",
            key,
            HashAlgorithmName.SHA256
        ).CreateSigningRequestPem();
    }

    /// <summary>Records certificate requests and answers them with a fixed response or error.</summary>
    private sealed class FakeEzcaClient : IEZCAClient
    {
        private readonly CertificateCreatedResponse? _response;
        private readonly Exception? _error;

        public FakeEzcaClient(CertificateCreatedResponse response)
        {
            _response = response;
        }

        public FakeEzcaClient(Exception error)
        {
            _error = error;
        }

        public sealed record Request(
            AvailableCAModel Ca,
            string Csr,
            string SubjectName,
            List<SubjectAltValue> SubjectAltNames,
            int ValidityDays,
            List<string>? Ekus
        );

        public sealed record DomainRegistration(
            AvailableCAModel Ca,
            string Domain,
            List<AADObjectModel> Owners,
            List<AADObjectModel> Administrators
        );

        public List<Request> Requests { get; } = [];

        public List<DomainInformationModel> Domains { get; init; } = [];

        public APIResultModel RegistrationResult { get; init; } = new(true);

        public List<DomainRegistration> Registrations { get; } = [];

        public Task<CertificateCreatedResponse?> RequestCertificateWithChainV2Async(
            AvailableCAModel ca,
            string csr,
            string subjectName,
            List<SubjectAltValue> subjectAlternateNames,
            int certificateValidityDays,
            string location = "Generate Locally",
            List<string>? ekus = null
        )
        {
            Requests.Add(
                new Request(
                    ca,
                    csr,
                    subjectName,
                    subjectAlternateNames,
                    certificateValidityDays,
                    ekus
                )
            );
            if (_error is not null)
            {
                throw _error;
            }
            return Task.FromResult(_response);
        }

        public Task<string> RenewCertificateAsync(X509Certificate2 cert, string csr)
        {
            throw new NotImplementedException();
        }

        public Task<CertificateCreatedResponse?> RenewCertificateWithChainAsync(
            X509Certificate2 cert,
            string csr
        )
        {
            throw new NotImplementedException();
        }

        public Task RevokeCertificateAsync(X509Certificate2 cert)
        {
            throw new NotImplementedException();
        }

        public Task<AvailableCAModel[]?> GetAvailableCAsAsync()
        {
            throw new NotImplementedException();
        }

        public Task<AvailableCAModel[]?> GetAvailableCAsForEZRadiusAsync()
        {
            throw new NotImplementedException();
        }

        public Task<X509Certificate2?> RequestCertificateAsync(
            AvailableCAModel ca,
            string domain,
            int certificateValidityDays,
            string location = "Generated Locally"
        )
        {
            throw new NotImplementedException();
        }

        public Task<X509Certificate2?> RequestCertificateAsync(
            AvailableCAModel ca,
            string csr,
            string subjectName,
            int certificateValidityDays
        )
        {
            throw new NotImplementedException();
        }

        public Task<X509Certificate2?> RequestDCCertificateAsync(
            AvailableCAModel ca,
            string csr,
            string subjectName,
            string dnsName,
            int certificateValidityDays,
            List<string> ekus,
            string dcGuid = "",
            string sid = ""
        )
        {
            throw new NotImplementedException();
        }

        public Task<APIResultModel> RegisterDomainAsync(
            AvailableCAModel ca,
            string domain,
            List<AADObjectModel>? owners = null,
            List<AADObjectModel>? certificateAdministrators = null,
            List<AADObjectModel>? requestersOnly = null,
            List<string>? notificationEmails = null
        )
        {
            Registrations.Add(new DomainRegistration(ca, domain, owners!, certificateAdministrators!));
            return Task.FromResult(RegistrationResult);
        }

        public Task<CertificateCreatedResponse?> RequestCertificateWithChainAsync(
            AvailableCAModel ca,
            string csr,
            string subjectName,
            List<string> subjectAlternateNames,
            int certificateValidityDays,
            string location = "Generate Locally"
        )
        {
            throw new NotImplementedException();
        }

        public Task<AvailableSelfServiceModel?> GetSelfServiceCertAvailableProfilesAsync()
        {
            throw new NotImplementedException();
        }

        public Task<APIResultModel> RegisterOnBehalfOfSelfServiceAgentAsync(
            DBSelfServiceScep scepAgent
        )
        {
            throw new NotImplementedException();
        }

        public Task<string> RequestCertificateOnBehalfOfUserAsync(
            X509Certificate2 agentCertificate,
            string csr,
            DBSelfServiceScep selfServiceProfile,
            string userGuid
        )
        {
            throw new NotImplementedException();
        }

        public Task<List<DomainInformationModel>> GetRegisteredDomainsAsync()
        {
            return Task.FromResult(Domains);
        }

        public Task<List<SSLCertInfoV2>> GetMyCertificatesAsync()
        {
            throw new NotImplementedException();
        }

        public Task<List<SSLCertInfoV2>> GetMyCertificatesPaginatedAsync(int pageNumber)
        {
            throw new NotImplementedException();
        }

        public Task<List<SSLCertAuditLogModel>> GetCertificateAuditLogsAsync(
            AuditRequestModel auditRequest
        )
        {
            throw new NotImplementedException();
        }

        public Task<CADetailsResponse?> GetCALocalCertificatesAsync(string tenantID, string caID)
        {
            throw new NotImplementedException();
        }
    }
}
