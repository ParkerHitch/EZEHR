using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>
/// The organizations table. The platform operator adds providers through register-provider; sysadmins edit or
/// delete rows by hand. Loaded per request (it's a handful of rows), so a change takes effect on the next request.
/// </summary>
public sealed class OrganizationManager(Db db, ILogger<OrganizationManager> logger)
{
    public async Task<OrganizationDirectory> LoadDirectoryAsync()
    {
        await using SqlConnection connection = await db.OpenAsync();
        IEnumerable<Organization> organizations = await connection.QueryAsync<Organization>(
            """
            SELECT uri AS Uri, kind AS Kind, display_name AS DisplayName,
                   certificate_cn AS CertificateCommonName, entra_tenant_id AS EntraTenantId
            FROM organizations
            """
        );
        return new OrganizationDirectory(organizations);
    }

    /// <summary>
    /// Adds a provider whose EZCA certificate has CN {slug}.providers.ezehr. Only the platform may. It has no Entra
    /// tenant, so it has no clinicians until a sysadmin sets one. Never overwrites an existing row.
    /// </summary>
    public async Task<RegisterProviderResult> RegisterProviderAsync(
        Caller caller,
        string slug,
        string displayName
    )
    {
        if (caller is UnverifiedCaller unverified)
        {
            return new RegisterProviderResult(false, unverified.Reason, null, null);
        }
        if (caller is not PlatformCaller)
        {
            return new RegisterProviderResult(false, DenyReason.WrongPartyKind, null, null);
        }

        Organization organization = new(
            OrgDirectory.ProviderUri(slug),
            PartyKind.Provider,
            displayName,
            OrgDirectory.ProviderCommonName(slug),
            null
        );
        await using SqlConnection connection = await db.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO organizations(uri, kind, display_name, certificate_cn, entra_tenant_id)
                VALUES (@Uri, @kind, @DisplayName, @CertificateCommonName, NULL)
                """,
                new
                {
                    organization.Uri,
                    kind = organization.Kind.ToString(),
                    organization.DisplayName,
                    organization.CertificateCommonName,
                }
            );
        }
        catch (SqlException exception) when (Db.IsUniqueViolation(exception))
        {
            return new RegisterProviderResult(
                true,
                null,
                OrganizationError.OrganizationExists,
                null
            );
        }
        logger.LogInformation(
            "Platform registered provider {Uri} ({DisplayName}), CN {CommonName}",
            organization.Uri,
            organization.DisplayName,
            organization.CertificateCommonName
        );
        return new RegisterProviderResult(true, null, null, organization);
    }
}
