using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>
/// Reads the organizations table, which sysadmins edit by hand until there is a registration endpoint. Loaded per
/// request (it's a handful of rows), so an edit takes effect on the next request.
/// </summary>
public sealed class OrganizationManager(Db db)
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
}
