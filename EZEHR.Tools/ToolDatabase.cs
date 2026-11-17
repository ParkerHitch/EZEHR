using EZEHR.Models;
using Microsoft.Data.SqlClient;

namespace EZEHR.Tools;

/// <summary>
/// The backstage door: the database in AZURE_SQL_CONNECTIONSTRING, as whoever is signed in with `az login`.
/// `source scripts/local.env` or `source scripts/azure.env` sets it.
/// </summary>
public static class ToolDatabase
{
    public static bool IsConfigured
    {
        get
        {
            return !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(AppOptions.ConnectionStringKey)
            );
        }
    }

    /// <summary>"ezehr on 127.0.0.1,1433", never the password.</summary>
    public static string Describe()
    {
        SqlConnectionStringBuilder builder = new(
            Environment.GetEnvironmentVariable(AppOptions.ConnectionStringKey)
        );
        return $"{builder.InitialCatalog} on {builder.DataSource}";
    }
}
