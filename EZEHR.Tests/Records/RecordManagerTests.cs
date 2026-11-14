using System.Security.Cryptography;
using System.Text;
using Dapper;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Microsoft.Data.SqlClient;

namespace EZEHR.Tests.Records;

public class RecordManagerTests : IAsyncLifetime
{
    private const string Jws = "eyJhbGciOiJFUzI1NiJ9.eyJyZXNvdXJjZVR5cGUiOiJPYnNlcnZhdGlvbiJ9.c2ln";

    private readonly CountingKeyWrapService _keyWrap = new(TestServices.LocalKeyWrapService());
    private TestDatabase _database = null!;
    private RecordManager _records = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
        _records = new TestServices(_database.Db, new(TestPki.Start), _keyWrap).Records;
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private async Task<T> QueryAsync<T>(string sql)
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        return await connection.QuerySingleAsync<T>(sql);
    }

    [Fact] // Review Focus 4
    public async Task Put_is_idempotent()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        (bool Stored, Guid EntryId) first = await _records.StoreEntryAsync(
            patient,
            "urn:provider:maple|Observation/egfr-1",
            "Observation",
            Jws
        );
        (bool Stored, Guid EntryId) second = await _records.StoreEntryAsync(
            patient,
            "urn:provider:maple|Observation/egfr-1",
            "Observation",
            Jws
        );
        Assert.True(first.Stored);
        Assert.False(second.Stored);
        Assert.Equal(first.EntryId, second.EntryId);
        Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM entries"));
    }

    [Fact] // Review Focus 4
    public async Task Concurrent_puts_store_once()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        (bool Stored, Guid EntryId)[] results = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ =>
                    _records.StoreEntryAsync(patient, "a|Observation/1", "Observation", Jws)
                )
        );
        Assert.Single(results, result => result.Stored);
        Assert.Single(results.Select(result => result.EntryId).Distinct());
        Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM entries"));
    }

    [Fact]
    public async Task Ciphertext_has_no_plaintext()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        await _records.StoreEntryAsync(
            patient,
            "urn:provider:maple|Observation/egfr-1",
            "Observation",
            Jws
        );
        string text = Encoding.UTF8.GetString(
            await QueryAsync<byte[]>("SELECT ciphertext FROM entries")
        );
        Assert.DoesNotContain("resourceType", text);
        Assert.DoesNotContain("eyJ", text);
        byte[] wrappedDataKey = await QueryAsync<byte[]>("SELECT wrapped_dek FROM patients");
        Assert.True(wrappedDataKey.Length > 32);
    }

    [Fact]
    public async Task Read_filters_by_type()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        await _records.StoreEntryAsync(patient, "a|Observation/1", "Observation", Jws + "1");
        await _records.StoreEntryAsync(patient, "a|Condition/1", "Condition", Jws + "2");
        Guid other = await _records.CreatePatientAsync("Someone Else");
        await _records.StoreEntryAsync(other, "a|Observation/1", "Observation", Jws + "3");

        IReadOnlyList<DecryptedEntry> all = await _records.DecryptEntriesAsync(patient, null);
        Assert.Equal(2, all.Count);
        IReadOnlyList<DecryptedEntry> labs = await _records.DecryptEntriesAsync(
            patient,
            resourceType => resourceType == "Observation"
        );
        Assert.Equal(Jws + "1", Assert.Single(labs).Jws);
    }

    [Fact]
    public async Task Read_unwraps_key_every_call()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        await _records.StoreEntryAsync(patient, "a|Observation/1", "Observation", Jws);
        int before = _keyWrap.Unwraps;
        await _records.DecryptEntriesAsync(patient, null);
        await _records.DecryptEntriesAsync(patient, null);
        Assert.Equal(before + 2, _keyWrap.Unwraps);
    }

    [Fact]
    public async Task Entry_cannot_be_moved_to_another_patient()
    {
        Guid first = await _records.CreatePatientAsync("A");
        Guid second = await _records.CreatePatientAsync("B");
        await _records.StoreEntryAsync(first, "a|Observation/1", "Observation", Jws);
        await using (SqlConnection connection = await _database.Db.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE entries SET patient_id = @second",
                new { second }
            );
        }
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            _records.DecryptEntriesAsync(second, null)
        );
    }

    [Fact]
    public async Task Unknown_patient_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _records.StoreEntryAsync(Guid.NewGuid(), "k", "Observation", Jws)
        );
    }

    private sealed class CountingKeyWrapService(IKeyWrapService inner) : IKeyWrapService
    {
        private int _unwraps;

        public int Unwraps
        {
            get { return _unwraps; }
        }

        public Task<byte[]> WrapAsync(byte[] dataKey)
        {
            return inner.WrapAsync(dataKey);
        }

        public Task<byte[]> UnwrapAsync(byte[] wrappedKey)
        {
            Interlocked.Increment(ref _unwraps);
            return inner.UnwrapAsync(wrappedKey);
        }
    }
}
