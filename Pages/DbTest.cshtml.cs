using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace EZEHR.Pages;

public class DbTestModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DbTestModel> _logger;

    public DbTestModel(IConfiguration configuration, ILogger<DbTestModel> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string? ServerName { get; private set; }
    public string? DatabaseName { get; private set; }
    public string? ErrorMessage { get; private set; }
    public List<TableInfo> Tables { get; } = new();

    public class TableInfo
    {
        public string Schema { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime Created { get; set; }
    }

    public async Task OnGetAsync()
    {
        var connectionString = _configuration["AZURE_SQL_CONNECTIONSTRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            ErrorMessage = "The AZURE_SQL_CONNECTIONSTRING app setting is not set.";
            return;
        }

        try
        {
            var csb = new SqlConnectionStringBuilder(connectionString);
            ServerName = csb.DataSource;
            DatabaseName = csb.InitialCatalog;

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.name AS SchemaName, t.name AS TableName, t.create_date AS Created
                FROM sys.tables t
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                ORDER BY s.name, t.name;
                """;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Tables.Add(new TableInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Created = reader.GetDateTime(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query the Azure SQL database.");
            ErrorMessage = ex.Message;
        }
    }
}
