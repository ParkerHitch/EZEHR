using Dapper;
using EZEHR.Data;
using Microsoft.Data.Sqlite;

namespace EZEHR.Tests.Data;

public class DbTests
{
    [Fact]
    public void Open_creates_all_tables()
    {
        using var db = Db.Open(":memory:");
        var tables = db.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").ToHashSet();
        foreach (var t in new[] { "patients", "patient_credentials", "invites", "provider_links", "entries",
                                  "share_offers", "grants", "audit_events", "local_clinicians" })
            Assert.Contains(t, tables);
    }

    [Fact]
    public void Entries_unique_per_patient_and_source_key()
    {
        using var db = Db.Open(":memory:");
        const string sql = "INSERT INTO entries(id, patient_id, source_key, resource_type, iv, ciphertext, created_at) " +
                           "VALUES (@id, 'p1', 'urn:provider:maple|Observation/1', 'Observation', x'00', x'00', '2026-11-13')";
        db.Execute(sql, new { id = Guid.NewGuid().ToString() });
        Assert.Throws<SqliteException>(() => db.Execute(sql, new { id = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public void Provider_links_unique_per_provider_patient_id()
    {
        using var db = Db.Open(":memory:");
        const string sql = "INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id) VALUES (@p, 'urn:provider:maple', 'MSF-0932')";
        db.Execute(sql, new { p = "p1" });
        Assert.Throws<SqliteException>(() => db.Execute(sql, new { p = "p2" }));
    }
}
