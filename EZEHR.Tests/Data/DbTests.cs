using Dapper;
using EZEHR.Data;
using Microsoft.Data.SqlClient;

namespace EZEHR.Tests.Data;

public class DbTests : IAsyncLifetime
{
    private const string InsertEntry = """
        INSERT INTO entries(id, patient_id, source_key, resource_type, iv, ciphertext, created_at)
        VALUES (NEWID(), @patientId, @sourceKey, 'Observation', 0x00, 0x00, SYSDATETIMEOFFSET())
        """;

    private const string InsertLink = """
        INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id)
        VALUES (@patientId, 'urn:provider:maple', 'MSF-0932')
        """;

    private TestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private async Task<HashSet<string>> TablesAsync()
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        IEnumerable<string> names = await connection.QueryAsync<string>(
            "SELECT name FROM sys.tables"
        );
        return names.ToHashSet();
    }

    [Fact]
    public async Task EnsureSchema_creates_all_tables()
    {
        HashSet<string> tables = await TablesAsync();
        string[] expected =
        [
            "patients",
            "patient_credentials",
            "invites",
            "provider_links",
            "entries",
            "share_offers",
            "grants",
            "audit_events",
            "local_clinicians",
        ];
        foreach (string table in expected)
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public async Task EnsureSchema_twice_is_harmless()
    {
        HashSet<string> before = await TablesAsync();
        await _database.Db.EnsureSchemaAsync();
        Assert.Equal(before, await TablesAsync());
    }

    [Fact]
    public async Task Entries_unique_per_patient_and_source_key()
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        object entry = new
        {
            patientId = Guid.NewGuid(),
            sourceKey = "urn:provider:maple|Observation/1",
        };
        await connection.ExecuteAsync(InsertEntry, entry);
        SqlException duplicate = await Assert.ThrowsAsync<SqlException>(() =>
            connection.ExecuteAsync(InsertEntry, entry)
        );
        Assert.True(Db.IsUniqueViolation(duplicate));
    }

    [Fact]
    public async Task Source_key_is_case_sensitive()
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        Guid patientId = Guid.NewGuid();
        await connection.ExecuteAsync(
            InsertEntry,
            new { patientId, sourceKey = "urn:provider:maple|Observation/a" }
        );
        await connection.ExecuteAsync(
            InsertEntry,
            new { patientId, sourceKey = "urn:provider:maple|Observation/A" }
        );
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entries"));
    }

    [Fact]
    public async Task Provider_links_unique_per_provider_patient_id()
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(InsertLink, new { patientId = Guid.NewGuid() });
        SqlException duplicate = await Assert.ThrowsAsync<SqlException>(() =>
            connection.ExecuteAsync(InsertLink, new { patientId = Guid.NewGuid() })
        );
        Assert.True(Db.IsUniqueViolation(duplicate));
    }
}
