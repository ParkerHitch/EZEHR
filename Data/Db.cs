using Microsoft.Data.Sqlite;

namespace EZEHR.Data;

/// <summary>Opens the SQLite database and creates any missing tables. Ids and times are stored as TEXT (Guid "D", ISO 8601 "O").</summary>
public static class Db
{
    public static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            DefaultTimeout = 10,
        }.ToString());
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            // Default rollback journal, not WAL: App Service's /home is a network share, where WAL's shared memory is unreliable.
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    public static string Time(DateTimeOffset t) => t.ToUniversalTime().ToString("O");

    public static DateTimeOffset Time(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? TimeOrNull(string? s) => s is null ? null : Time(s);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS patients(
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            wrapped_dek BLOB NOT NULL,
            created_at TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS patient_credentials(
            credential_id BLOB PRIMARY KEY,
            patient_id TEXT NOT NULL,
            public_key BLOB NOT NULL,
            sign_count INTEGER NOT NULL,
            user_handle BLOB NOT NULL);

        CREATE TABLE IF NOT EXISTS invites(
            code TEXT PRIMARY KEY,
            provider_uri TEXT NOT NULL,
            provider_patient_id TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            redeemed_at TEXT);

        CREATE TABLE IF NOT EXISTS provider_links(
            patient_id TEXT NOT NULL,
            provider_uri TEXT NOT NULL,
            provider_patient_id TEXT NOT NULL,
            UNIQUE(provider_uri, provider_patient_id));

        CREATE TABLE IF NOT EXISTS entries(
            id TEXT PRIMARY KEY,
            patient_id TEXT NOT NULL,
            source_key TEXT NOT NULL,
            resource_type TEXT NOT NULL,
            iv BLOB NOT NULL,
            ciphertext BLOB NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(patient_id, source_key));

        CREATE TABLE IF NOT EXISTS share_offers(
            id TEXT PRIMARY KEY,
            patient_id TEXT NOT NULL,
            code TEXT NOT NULL UNIQUE,
            duration TEXT NOT NULL,
            offer_expires_at TEXT NOT NULL,
            redeemed_grant_id TEXT);

        CREATE TABLE IF NOT EXISTS grants(
            id TEXT PRIMARY KEY,
            patient_id TEXT NOT NULL,
            grantee_kind TEXT NOT NULL,
            grantee_uri TEXT NOT NULL,
            grantee_name TEXT NOT NULL,
            grantee_org TEXT NOT NULL,
            scope TEXT NOT NULL,
            purpose TEXT NOT NULL,
            status TEXT NOT NULL,
            duration_minutes INTEGER,
            created_at TEXT NOT NULL,
            approved_at TEXT,
            expires_at TEXT,
            revoked_at TEXT);

        CREATE TABLE IF NOT EXISTS audit_events(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            patient_id TEXT NOT NULL,
            at TEXT NOT NULL,
            actor_kind TEXT,
            actor_uri TEXT,
            actor_name TEXT,
            actor_org TEXT,
            action TEXT NOT NULL,
            claimed_purpose TEXT,
            granted INTEGER NOT NULL,
            reason TEXT);
        CREATE INDEX IF NOT EXISTS audit_by_patient ON audit_events(patient_id, id);

        CREATE TABLE IF NOT EXISTS local_clinicians(
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            org_name TEXT NOT NULL,
            credential_id BLOB,
            public_key BLOB,
            sign_count INTEGER NOT NULL DEFAULT 0);
        """;
}
