using System.Security.Cryptography;
using Dapper;
using EZEHR.Data;
using EZEHR.Security.Crypto;
using Microsoft.Data.Sqlite;

namespace EZEHR.Records;

public sealed record StoredEntry(Guid Id, string ResourceType, string Jws);

/// <summary>
/// Stores signed records encrypted under the patient's own key (envelope encryption). The patient key is unwrapped
/// on every call, so every read is a Key Vault operation. Only gate-checked code and the patient's own pages call ReadAsync.
/// </summary>
public sealed class RecordStore(SqliteConnection db, IKeyWrapper wrapper, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>Generates a new patient data key and returns it wrapped. The plaintext key is erased.</summary>
    public async Task<byte[]> CreatePatientKeyAsync()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        try { return await wrapper.WrapAsync(dek); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    /// <summary>Creates a patient row with a fresh wrapped data key.</summary>
    public async Task<Guid> CreatePatientAsync(string displayName)
    {
        var id = Guid.NewGuid();
        var wrapped = await CreatePatientKeyAsync();
        db.Execute("INSERT INTO patients(id, display_name, wrapped_dek, created_at) VALUES (@id, @name, @wrapped, @at)",
            new { id = id.ToString(), name = displayName, wrapped, at = Db.Time(time.GetUtcNow()) });
        return id;
    }

    /// <summary>Stores a signed record once per (patient, sourceKey). A repeat returns the existing id with Inserted = false.</summary>
    public async Task<(bool Inserted, Guid Id)> PutAsync(Guid patientId, string sourceKey, string resourceType, string jws)
    {
        var existing = Existing(patientId, sourceKey);
        if (existing is { } found) return (false, found);

        var dek = await UnwrapAsync(patientId);
        try
        {
            var id = Guid.NewGuid();
            var (iv, ciphertext) = RecordCipher.Encrypt(dek, Aad(patientId, id), jws);
            var rows = db.Execute("""
                INSERT OR IGNORE INTO entries(id, patient_id, source_key, resource_type, iv, ciphertext, created_at)
                VALUES (@id, @patient, @sourceKey, @resourceType, @iv, @ciphertext, @at)
                """,
                new { id = id.ToString(), patient = patientId.ToString(), sourceKey, resourceType, iv, ciphertext, at = Db.Time(time.GetUtcNow()) });
            return rows == 1 ? (true, id) : (false, Existing(patientId, sourceKey)!.Value);
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public async Task<IReadOnlyList<StoredEntry>> ReadAsync(Guid patientId, Func<string, bool>? resourceTypeFilter)
    {
        var dek = await UnwrapAsync(patientId);
        try
        {
            var rows = db.Query<(string Id, string ResourceType, byte[] Iv, byte[] Ciphertext)>(
                "SELECT id, resource_type, iv, ciphertext FROM entries WHERE patient_id = @patient ORDER BY created_at, id",
                new { patient = patientId.ToString() });
            var result = new List<StoredEntry>();
            foreach (var r in rows)
            {
                if (resourceTypeFilter is not null && !resourceTypeFilter(r.ResourceType)) continue;
                var id = Guid.Parse(r.Id);
                result.Add(new StoredEntry(id, r.ResourceType, RecordCipher.Decrypt(dek, Aad(patientId, id), r.Iv, r.Ciphertext)));
            }
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public bool PatientExists(Guid patientId) =>
        db.ExecuteScalar<int>("SELECT COUNT(*) FROM patients WHERE id = @id", new { id = patientId.ToString() }) == 1;

    private Guid? Existing(Guid patientId, string sourceKey)
    {
        var id = db.QuerySingleOrDefault<string>("SELECT id FROM entries WHERE patient_id = @patient AND source_key = @sourceKey",
            new { patient = patientId.ToString(), sourceKey });
        return id is null ? null : Guid.Parse(id);
    }

    private async Task<byte[]> UnwrapAsync(Guid patientId)
    {
        var wrapped = db.QuerySingleOrDefault<byte[]>("SELECT wrapped_dek FROM patients WHERE id = @id", new { id = patientId.ToString() })
                      ?? throw new InvalidOperationException("Unknown patient.");
        return await wrapper.UnwrapAsync(wrapped);
    }

    private static string Aad(Guid patientId, Guid entryId) => $"{patientId}:{entryId}";
}
