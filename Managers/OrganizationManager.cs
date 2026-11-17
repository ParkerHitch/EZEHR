using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>
/// The organizations table. The platform operator adds providers through register-provider and apps through
/// register-app; sysadmins edit or delete rows by hand. Loaded per request (it's a handful of rows), so a change takes effect on the next request.
/// </summary>
public sealed class OrganizationManager(Db db, ILogger<OrganizationManager> logger)
{
    public async Task<OrganizationDirectory> LoadDirectoryAsync()
    {
        await using SqlConnection connection = await db.OpenAsync();
        IEnumerable<Organization> organizations = await connection.QueryAsync<Organization>(
            """
            SELECT uri AS Uri, kind AS Kind, display_name AS DisplayName,
                   certificate_cn AS CertificateCommonName, entra_tenant_id AS EntraTenantId,
                   app_scope AS AppScope, app_description AS AppDescription
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

    /// <summary>
    /// Adds an app whose EZCA certificate has CN {slug}.apps.ezehr, with the scope it declares and the description
    /// patients read. Only the platform may. Being listed gives it nothing: each patient still has to turn it on.
    /// Never overwrites an existing row.
    /// </summary>
    public async Task<RegisterAppResult> RegisterAppAsync(
        Caller caller,
        string slug,
        string displayName,
        GrantScope scope,
        string description
    )
    {
        if (caller is UnverifiedCaller unverified)
        {
            return new RegisterAppResult(false, unverified.Reason, null, null);
        }
        if (caller is not PlatformCaller)
        {
            return new RegisterAppResult(false, DenyReason.WrongPartyKind, null, null);
        }
        if (scope == GrantScope.Full)
        {
            return new RegisterAppResult(true, null, OrganizationError.AppScopeNotAllowed, null);
        }

        Organization organization = new(
            OrgDirectory.AppUri(slug),
            PartyKind.App,
            displayName,
            OrgDirectory.AppCommonName(slug),
            null,
            scope,
            description
        );
        await using SqlConnection connection = await db.OpenAsync();
        try
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO organizations(uri, kind, display_name, certificate_cn, entra_tenant_id,
                                          app_scope, app_description)
                VALUES (@Uri, @kind, @DisplayName, @CertificateCommonName, NULL, @appScope, @AppDescription)
                """,
                new
                {
                    organization.Uri,
                    kind = organization.Kind.ToString(),
                    organization.DisplayName,
                    organization.CertificateCommonName,
                    appScope = scope.ToString(),
                    organization.AppDescription,
                }
            );
        }
        catch (SqlException exception) when (Db.IsUniqueViolation(exception))
        {
            return new RegisterAppResult(true, null, OrganizationError.OrganizationExists, null);
        }
        logger.LogInformation(
            "Platform registered app {Uri} ({DisplayName}), CN {CommonName}, scope {Scope}",
            organization.Uri,
            organization.DisplayName,
            organization.CertificateCommonName,
            scope
        );
        return new RegisterAppResult(true, null, null, organization);
    }
}
