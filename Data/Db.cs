using EZEHR.Models;
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

    /// <summary>Creates the tables, constraints and indexes that are missing. Safe to run on every start.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken);
        await using SqlCommand command = new(Schema, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>True for a duplicate key in a unique index (2601) or a unique or primary key constraint (2627).</summary>
    public static bool IsUniqueViolation(SqlException exception)
    {
        return exception.Number is 2601 or 2627;
    }

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
            CONSTRAINT pk_provider_links PRIMARY KEY (provider_uri, provider_patient_id));

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

        IF OBJECT_ID(N'dbo.local_clinicians') IS NULL
        CREATE TABLE dbo.local_clinicians(
            id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            display_name NVARCHAR(200) NOT NULL,
            org_name NVARCHAR(200) NOT NULL,
            credential_id VARBINARY(900) NULL,
            public_key VARBINARY(MAX) NULL,
            sign_count BIGINT NOT NULL DEFAULT 0);
        """;
}
