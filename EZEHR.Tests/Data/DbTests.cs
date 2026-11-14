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
            "organizations",
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

    [Fact]
    public async Task DeleteAllData_empties_every_table_and_keeps_them()
    {
        await using (SqlConnection connection = await _database.Db.OpenAsync())
        {
            Guid patientId = Guid.NewGuid();
            await connection.ExecuteAsync(InsertEntry, new { patientId, sourceKey = "a" });
            await connection.ExecuteAsync(InsertLink, new { patientId });
        }

        await _database.Db.DeleteAllDataAsync();

        await using SqlConnection check = await _database.Db.OpenAsync();
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM entries"));
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM provider_links"));
        Assert.Contains("entries", await TablesAsync());
    }

    [Fact]
    public async Task EnsureSchema_seeds_demo_organizations_once_and_keeps_edits()
    {
        Guid tenantId = Guid.NewGuid();
        await using SqlConnection connection = await _database.Db.OpenAsync();
        Assert.Equal(
            5,
            await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM organizations")
        );
        await connection.ExecuteAsync(
            """
            UPDATE organizations SET entra_tenant_id = @tenantId, display_name = N'Riverside Health'
            WHERE uri = 'urn:provider:riverside'
            """,
            new { tenantId }
        );

        await _database.Db.EnsureSchemaAsync();
        await _database.Db.DeleteAllDataAsync();

        Assert.Equal(
            5,
            await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM organizations")
        );
        (Guid? EntraTenantId, string DisplayName) riverside = await connection.QuerySingleAsync<(
            Guid?,
            string
        )>(
            """
            SELECT entra_tenant_id, display_name FROM organizations
            WHERE uri = 'urn:provider:riverside'
            """
        );
        Assert.Equal(tenantId, riverside.EntraTenantId);
        Assert.Equal("Riverside Health", riverside.DisplayName);
    }

    [Fact]
    public async Task Tenant_belongs_to_one_provider_only()
    {
        const string SetTenant =
            "UPDATE organizations SET entra_tenant_id = @tenantId WHERE uri = @uri";
        Guid tenantId = Guid.NewGuid();
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(SetTenant, new { tenantId, uri = "urn:provider:riverside" });

        SqlException duplicate = await Assert.ThrowsAsync<SqlException>(() =>
            connection.ExecuteAsync(SetTenant, new { tenantId, uri = "urn:provider:maple" })
        );
        Assert.True(Db.IsUniqueViolation(duplicate));
        await Assert.ThrowsAsync<SqlException>(() =>
            connection.ExecuteAsync(
                SetTenant,
                new { tenantId = Guid.NewGuid(), uri = "urn:app:kidney-check" }
            )
        );
    }

    [Fact]
    public async Task EnsureSchema_adds_org_columns_to_existing_tables()
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        // Shape the tables as a database deployed before these columns existed.
        await connection.ExecuteAsync("ALTER TABLE dbo.grants DROP COLUMN grantee_org_uri");
        await connection.ExecuteAsync("DROP INDEX ix_audit_events_actor_org ON dbo.audit_events");
        await connection.ExecuteAsync("ALTER TABLE dbo.audit_events DROP COLUMN actor_org_uri");

        await _database.Db.EnsureSchemaAsync();
        await _database.Db.EnsureSchemaAsync();

        Assert.NotNull(
            await connection.ExecuteScalarAsync<int?>(
                "SELECT COL_LENGTH('dbo.grants', 'grantee_org_uri')"
            )
        );
        Assert.NotNull(
            await connection.ExecuteScalarAsync<int?>(
                "SELECT COL_LENGTH('dbo.audit_events', 'actor_org_uri')"
            )
        );
        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM sys.indexes
                WHERE name = 'ix_audit_events_actor_org' AND object_id = OBJECT_ID('dbo.audit_events')
                """
            )
        );
    }

    [Fact]
    public async Task Deleted_organization_stays_deleted_after_restart()
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM organizations WHERE uri = 'urn:app:kidney-check'"
        );

        await _database.Db.EnsureSchemaAsync();

        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM organizations WHERE uri = 'urn:app:kidney-check'"
            )
        );
    }

    [Fact]
    public async Task Restart_survives_a_demo_cn_reused_by_another_organization()
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        await connection.ExecuteAsync(
            """
            DELETE FROM organizations WHERE uri = 'urn:provider:riverside';
            INSERT INTO organizations(uri, kind, display_name, certificate_cn)
            VALUES ('urn:provider:riverside-health', 'Provider', N'Riverside Health',
                    'riverside.providers.ezehr');
            """
        );

        await _database.Db.EnsureSchemaAsync();

        Assert.Equal(
            5,
            await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM organizations")
        );
    }
}
