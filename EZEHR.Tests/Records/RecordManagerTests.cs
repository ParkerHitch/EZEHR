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
        (bool Inserted, Guid Id) first = await _records.PutAsync(
            patient,
            "urn:provider:maple|Observation/egfr-1",
            "Observation",
            Jws
        );
        (bool Inserted, Guid Id) second = await _records.PutAsync(
            patient,
            "urn:provider:maple|Observation/egfr-1",
            "Observation",
            Jws
        );
        Assert.True(first.Inserted);
        Assert.False(second.Inserted);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM entries"));
    }

    [Fact] // Review Focus 4
    public async Task Concurrent_puts_store_once()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        (bool Inserted, Guid Id)[] results = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ => _records.PutAsync(patient, "a|Observation/1", "Observation", Jws))
        );
        Assert.Single(results, result => result.Inserted);
        Assert.Single(results.Select(result => result.Id).Distinct());
        Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM entries"));
    }

    [Fact]
    public async Task Ciphertext_has_no_plaintext()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        await _records.PutAsync(
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
        byte[] wrappedDek = await QueryAsync<byte[]>("SELECT wrapped_dek FROM patients");
        Assert.True(wrappedDek.Length > 32);
    }

    [Fact]
    public async Task Read_filters_by_type()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        await _records.PutAsync(patient, "a|Observation/1", "Observation", Jws + "1");
        await _records.PutAsync(patient, "a|Condition/1", "Condition", Jws + "2");
        Guid other = await _records.CreatePatientAsync("Someone Else");
        await _records.PutAsync(other, "a|Observation/1", "Observation", Jws + "3");

        IReadOnlyList<StoredEntry> all = await _records.ReadAsync(patient, null);
        Assert.Equal(2, all.Count);
        IReadOnlyList<StoredEntry> labs = await _records.ReadAsync(patient, resourceType =>
            resourceType == "Observation"
        );
        Assert.Equal(Jws + "1", Assert.Single(labs).Jws);
    }

    [Fact]
    public async Task Read_unwraps_key_every_call()
    {
        Guid patient = await _records.CreatePatientAsync("Sam Taylor");
        await _records.PutAsync(patient, "a|Observation/1", "Observation", Jws);
        int before = _keyWrap.Unwraps;
        await _records.ReadAsync(patient, null);
        await _records.ReadAsync(patient, null);
        Assert.Equal(before + 2, _keyWrap.Unwraps);
    }

    [Fact]
    public async Task Entry_cannot_be_moved_to_another_patient()
    {
        Guid first = await _records.CreatePatientAsync("A");
        Guid second = await _records.CreatePatientAsync("B");
        await _records.PutAsync(first, "a|Observation/1", "Observation", Jws);
        await using (SqlConnection connection = await _database.Db.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE entries SET patient_id = @second",
                new { second }
            );
        }
        await Assert.ThrowsAnyAsync<CryptographicException>(() => _records.ReadAsync(second, null));
    }

    [Fact]
    public async Task Unknown_patient_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _records.PutAsync(Guid.NewGuid(), "k", "Observation", Jws)
        );
    }

    private sealed class CountingKeyWrapService(IKeyWrapService inner) : IKeyWrapService
    {
        private int _unwraps;

        public int Unwraps
        {
            get { return _unwraps; }
        }

        public Task<byte[]> WrapAsync(byte[] dek)
        {
            return inner.WrapAsync(dek);
        }

        public Task<byte[]> UnwrapAsync(byte[] wrapped)
        {
            Interlocked.Increment(ref _unwraps);
            return inner.UnwrapAsync(wrapped);
        }
    }
}
