using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EZEHR.Tools;

/// <summary>
/// The backstage door: the database in AZURE_SQL_CONNECTIONSTRING, as whoever is signed in with `az login`, never
/// the network. `source scripts/local.env` or `source scripts/azure.env` sets it.
/// </summary>
public static class ToolDatabase
{
    /// <summary>The charts Sam has at the demo providers; whoever redeemed an invite for one of them is Sam.</summary>
    private static readonly (string Slug, string Chart)[] SamsCharts =
    [
        ("northwind", "NWH-88213"),
        ("lakeside", "LUC-20417"),
        ("maple", "MSF-0932"),
        ("riverside", "RPC-1107"),
    ];

    public static bool IsConfigured
    {
        get
        {
            return !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(AppOptions.ConnectionStringKey)
            );
        }
    }

    public static Db Open()
    {
        if (!IsConfigured)
        {
            throw new ToolException(
                $"{AppOptions.ConnectionStringKey} isn't set, so the tools can't reach the database. Run: source scripts/local.env (or scripts/azure.env for the deployed site)"
            );
        }
        return new Db(new ConfigurationBuilder().AddEnvironmentVariables().Build());
    }

    /// <summary>"ezehr on 127.0.0.1,1433", never the password.</summary>
    public static string Describe()
    {
        SqlConnectionStringBuilder builder = new(
            Environment.GetEnvironmentVariable(AppOptions.ConnectionStringKey)
        );
        return $"{builder.InitialCatalog} on {builder.DataSource}";
    }

    public static async Task<Guid?> FindSamAsync()
    {
        foreach ((string slug, string chart) in SamsCharts)
        {
            Guid? patientId = await FindByChartAsync(slug, chart);
            if (patientId is not null)
            {
                return patientId;
            }
        }
        return null;
    }

    /// <summary>The EZEHR patient linked to the provider's chart, or null when nobody redeemed its invite.</summary>
    public static async Task<Guid?> FindByChartAsync(string providerSlug, string chart)
    {
        await using SqlConnection connection = await Open().OpenAsync();
        return await connection.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT patient_id FROM provider_links
            WHERE provider_uri = @providerUri AND provider_patient_id = @chart
            """,
            new { providerUri = OrgDirectory.ProviderUri(providerSlug), chart }
        );
    }
}
