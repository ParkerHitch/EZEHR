using System.Security.Cryptography.X509Certificates;
using EZCAClient.Managers;
using EZCAClient.Models;
using EZCAClient.Services;
using EZEHR.Models;

namespace EZEHR.Services;

public sealed class CertificateAuthorityException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Issues certificates from our EZCA issuing CA with Keytos's SDK. Only the laptop tool (certs issue --ca ezca)
/// calls it; the server never talks to EZCA. The SDK signs in with the TokenCredential it was built with.
/// </summary>
public sealed class EzcaCertificateAuthorityService(IEZCAClient client, AvailableCAModel ca)
    : ICertificateAuthorityService
{
    /// <summary>
    /// EZCA only issues for names registered on the CA. Registers the domain, with the given account as owner and
    /// certificate administrator, unless it already is. True when it registered it; a new domain works at once.
    /// </summary>
    public async Task<bool> EnsureDomainRegisteredAsync(
        string domain,
        string administratorObjectId,
        string administratorName
    )
    {
        List<DomainInformationModel> registered = await client.GetRegisteredDomainsAsync() ?? [];
        if (
            registered.Any(candidate =>
                candidate.CAID == ca.CAID
                && string.Equals(candidate.Domain, domain, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return false;
        }
        // Reuse the account exactly as EZCA lists it on another domain when it can, so its type is EZCA's own.
        AADObjectModel administrator =
            registered
                .SelectMany(candidate => candidate.DomainOwners ?? [])
                .FirstOrDefault(owner => owner.ObjectId == administratorObjectId)
            ?? new AADObjectModel(administratorObjectId, administratorName);
        APIResultModel result;
        try
        {
            result = await client.RegisterDomainAsync(
                ca,
                domain,
                [administrator],
                [administrator],
                [],
                []
            );
        }
        catch (HttpRequestException exception)
        {
            throw new CertificateAuthorityException(
                $"EZCA refused to register {domain}: {exception.Message}",
                exception
            );
        }
        if (!result.Success)
        {
            throw new CertificateAuthorityException(
                $"EZCA refused to register {domain}: {result.Message}"
            );
        }
        return true;
    }

    public async Task<IssuedCertificate> IssueAsync(
        string csrPem,
        string subjectName,
        int validityDays,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        CertificateCreatedResponse? response;
        try
        {
            response = await client.RequestCertificateWithChainV2Async(
                ca,
                csrPem,
                subjectName,
                [new SubjectAltValue(subjectName)],
                validityDays,
                ekus: [CertificateValidator.ClientAuthenticationOid]
            );
        }
        catch (HttpRequestException exception)
        {
            // The SDK puts EZCA's response body (or the transport error) in the message.
            throw new CertificateAuthorityException(
                $"EZCA refused the certificate for {subjectName}: {exception.Message}",
                exception
            );
        }
        if (response is null || string.IsNullOrWhiteSpace(response.CertificatePEM))
        {
            throw new CertificateAuthorityException(
                $"EZCA returned no certificate for {subjectName}."
            );
        }

        List<X509Certificate2> chain = [];
        foreach (string pem in new[] { response.IssuingCACertificate, response.RootCertificate })
        {
            if (!string.IsNullOrWhiteSpace(pem))
            {
                chain.Add(CryptoStaticService.ImportCertFromPEMString(pem));
            }
        }
        return new IssuedCertificate(
            CryptoStaticService.ImportCertFromPEMString(response.CertificatePEM),
            chain
        );
    }
}
