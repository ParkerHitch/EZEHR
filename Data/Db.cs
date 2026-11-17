using Dapper;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;

namespace EZEHR.Data;

/// <summary>
/// Opens Azure SQL connections and creates any missing tables. Open one connection per operation and dispose
/// it; pooling makes that cheap. The connection string comes from AZURE_SQL_CONNECTIONSTRING and holds no
/// password: App Service signs in with its managed identity, laptops with Active Directory Default.
/// </summary>
public sealed class Db(IConfiguration configuration)
{
    private readonly string _connectionString = configuration[AppOptions.ConnectionStringKey]
        ?? throw new InvalidOperationException(
            $"{AppOptions.ConnectionStringKey} is not set. Locally, put the ezehr-dev connection string in appsettings.Development.json."
        );

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        SqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Creates the tables, constraints and indexes that are missing, and seeds the demo organizations into an empty
    /// organizations table. Safe to run on every start: once the table has rows it is never touched, so sysadmins'
    /// edits and deletions stay.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using (SqlCommand command = new(Schema, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        // Seed only an empty table (a new database). Once a sysadmin has edited it, the table is theirs: deleting a
        // row is how an organization is cut off, so it must stay deleted across restarts.
        bool hasOrganizations = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.organizations) THEN 1 ELSE 0 END",
                cancellationToken: cancellationToken
            )
        );
        foreach (
            Organization organization in hasOrganizations ? [] : OrgDirectory.DemoOrganizations
        )
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO dbo.organizations(uri, kind, display_name, certificate_cn, entra_tenant_id)
                    VALUES (@Uri, @kind, @DisplayName, @CertificateCommonName, NULL);
                    """,
                    new
                    {
                        organization.Uri,
                        kind = organization.Kind.ToString(),
                        organization.DisplayName,
                        organization.CertificateCommonName,
                    },
                    cancellationToken: cancellationToken
                )
            );
        }
        foreach (string migration in Migrations)
        {
            await using SqlCommand command = new(migration, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Deletes every row and keeps the tables. Only `ezehr-tools demo reset` calls this, from a laptop signed in
    /// to the database; the site has no reset endpoint. Leaves organizations alone: that's configuration, not data.
    /// </summary>
    public async Task DeleteAllDataAsync(CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using SqlCommand command = new(
            """
            DELETE FROM dbo.audit_events;
            DELETE FROM dbo.grants;
            DELETE FROM dbo.share_offers;
            DELETE FROM dbo.entries;
            DELETE FROM dbo.provider_links;
            DELETE FROM dbo.invites;
            DELETE FROM dbo.patient_credentials;
            DELETE FROM dbo.patients;
            """,
            connection
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>True for a duplicate key in a unique index (2601) or a unique or primary key constraint (2627).</summary>
    public static bool IsUniqueViolation(SqlException exception)
    {
        return exception.Number is 2601 or 2627;
    }

    // Changes to tables that already exist in deployed databases, run in order after Schema. New columns and indexes
    // go here, never into the CREATE TABLE text, so fresh and deployed databases take the same path. One command per
    // entry: SQL Server compiles a whole batch first, so a statement can't use a column added earlier in its batch.
    private static readonly string[] Migrations =
    [
        """
            IF COL_LENGTH(N'dbo.grants', N'grantee_org_uri') IS NULL
            ALTER TABLE dbo.grants ADD grantee_org_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL;
            """,
        """
            IF COL_LENGTH(N'dbo.audit_events', N'actor_org_uri') IS NULL
            ALTER TABLE dbo.audit_events ADD actor_org_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL;
            """,
        """
            IF NOT EXISTS (SELECT 1 FROM sys.indexes
                           WHERE name = N'ix_audit_events_actor_org' AND object_id = OBJECT_ID(N'dbo.audit_events'))
            CREATE INDEX ix_audit_events_actor_org ON dbo.audit_events(actor_org_uri, id);
            """,
        """
            IF COL_LENGTH(N'dbo.patients', N'emergency_code') IS NULL
            ALTER TABLE dbo.patients ADD emergency_code NVARCHAR(16) NULL;
            """,
        """
            IF NOT EXISTS (SELECT 1 FROM sys.indexes
                           WHERE name = N'uq_patients_emergency_code' AND object_id = OBJECT_ID(N'dbo.patients'))
            CREATE UNIQUE INDEX uq_patients_emergency_code ON dbo.patients(emergency_code)
            WHERE emergency_code IS NOT NULL;
            """,
        """
            IF COL_LENGTH(N'dbo.grants', N'emergency_reason') IS NULL
            ALTER TABLE dbo.grants ADD emergency_reason NVARCHAR(200) NULL;
            """,
    ];

    // Identifier columns use a binary collation, because SQL Server's default collation is case-insensitive
    // and would make "Observation/a" and "Observation/A" the same source key.
    private const string Schema = """
        IF OBJECT_ID(N'dbo.patients') IS NULL
        CREATE TABLE dbo.patients(
            id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            display_name NVARCHAR(200) NOT NULL,
            wrapped_dek VARBINARY(MAX) NOT NULL,
            created_at DATETIMEOFFSET NOT NULL);

        IF OBJECT_ID(N'dbo.patient_credentials') IS NULL
        CREATE TABLE dbo.patient_credentials(
            credential_id VARBINARY(900) NOT NULL PRIMARY KEY,
            patient_id UNIQUEIDENTIFIER NOT NULL INDEX ix_patient_credentials_patient,
            public_key VARBINARY(MAX) NOT NULL,
            sign_count BIGINT NOT NULL,
            user_handle VARBINARY(64) NOT NULL);

        IF OBJECT_ID(N'dbo.invites') IS NULL
        CREATE TABLE dbo.invites(
            code NVARCHAR(16) NOT NULL PRIMARY KEY,
            provider_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            provider_patient_id NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            expires_at DATETIMEOFFSET NOT NULL,
            redeemed_at DATETIMEOFFSET NULL);

        IF OBJECT_ID(N'dbo.provider_links') IS NULL
        CREATE TABLE dbo.provider_links(
            patient_id UNIQUEIDENTIFIER NOT NULL INDEX ix_provider_links_patient,
            provider_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            provider_patient_id NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            linked_at DATETIMEOFFSET NULL,
            CONSTRAINT pk_provider_links PRIMARY KEY (provider_uri, provider_patient_id));

        -- Added after the table was first deployed; links made before then have no date.
        IF COL_LENGTH(N'dbo.provider_links', N'linked_at') IS NULL
        ALTER TABLE dbo.provider_links ADD linked_at DATETIMEOFFSET NULL;

        IF OBJECT_ID(N'dbo.entries') IS NULL
        CREATE TABLE dbo.entries(
            id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            patient_id UNIQUEIDENTIFIER NOT NULL,
            source_key NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            resource_type NVARCHAR(64) NOT NULL,
            iv VARBINARY(MAX) NOT NULL,
            ciphertext VARBINARY(MAX) NOT NULL,
            created_at DATETIMEOFFSET NOT NULL,
            CONSTRAINT uq_entries_patient_source UNIQUE (patient_id, source_key));

        IF OBJECT_ID(N'dbo.share_offers') IS NULL
        CREATE TABLE dbo.share_offers(
            id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            patient_id UNIQUEIDENTIFIER NOT NULL INDEX ix_share_offers_patient,
            code NVARCHAR(16) NOT NULL CONSTRAINT uq_share_offers_code UNIQUE,
            duration NVARCHAR(32) NOT NULL,
            offer_expires_at DATETIMEOFFSET NOT NULL,
            redeemed_grant_id UNIQUEIDENTIFIER NULL);

        IF OBJECT_ID(N'dbo.grants') IS NULL
        CREATE TABLE dbo.grants(
            id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            patient_id UNIQUEIDENTIFIER NOT NULL,
            grantee_kind NVARCHAR(32) NOT NULL,
            grantee_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            grantee_name NVARCHAR(200) NOT NULL,
            grantee_org NVARCHAR(200) NOT NULL,
            scope NVARCHAR(32) NOT NULL,
            purpose NVARCHAR(200) NOT NULL,
            status NVARCHAR(32) NOT NULL,
            duration_minutes INT NULL,
            created_at DATETIMEOFFSET NOT NULL,
            approved_at DATETIMEOFFSET NULL,
            expires_at DATETIMEOFFSET NULL,
            revoked_at DATETIMEOFFSET NULL,
            INDEX ix_grants_patient_grantee (patient_id, grantee_uri));

        IF OBJECT_ID(N'dbo.audit_events') IS NULL
        CREATE TABLE dbo.audit_events(
            id BIGINT IDENTITY(1, 1) NOT NULL PRIMARY KEY,
            patient_id UNIQUEIDENTIFIER NOT NULL,
            at DATETIMEOFFSET NOT NULL,
            actor_kind NVARCHAR(32) NULL,
            actor_uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            actor_name NVARCHAR(200) NULL,
            actor_org NVARCHAR(200) NULL,
            action NVARCHAR(32) NOT NULL,
            claimed_purpose NVARCHAR(200) NULL,
            granted BIT NOT NULL,
            reason NVARCHAR(32) NULL,
            INDEX ix_audit_events_patient (patient_id, id));

        -- Made for the clinician passkey fallback (plan Task 8b), which Entra sign-in made unnecessary. Nothing
        -- ever wrote to it; drop it while it's still empty.
        IF OBJECT_ID(N'dbo.local_clinicians') IS NOT NULL
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM dbo.local_clinicians)
            DROP TABLE dbo.local_clinicians;
        END;

        -- Who each organization is. The platform adds providers (register-provider); sysadmins edit by hand. The CN is
        -- compared without case, like the DNS name it is; a tenant belongs to at most one provider.
        IF OBJECT_ID(N'dbo.organizations') IS NULL
        CREATE TABLE dbo.organizations(
            uri NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            kind NVARCHAR(32) NOT NULL CONSTRAINT ck_organizations_kind CHECK (kind IN (N'Provider', N'App')),
            display_name NVARCHAR(200) NOT NULL,
            certificate_cn NVARCHAR(200) COLLATE Latin1_General_100_CI_AS NOT NULL
                CONSTRAINT uq_organizations_cn UNIQUE,
            entra_tenant_id UNIQUEIDENTIFIER NULL,
            CONSTRAINT ck_organizations_tenant_provider CHECK (entra_tenant_id IS NULL OR kind = N'Provider'),
            INDEX uq_organizations_tenant UNIQUE (entra_tenant_id) WHERE entra_tenant_id IS NOT NULL);
        """;
}
