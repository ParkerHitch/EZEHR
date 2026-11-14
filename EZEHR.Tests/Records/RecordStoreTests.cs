using System.Security.Cryptography;
using System.Text;
using Dapper;
using EZEHR.Data;
using EZEHR.Records;
using EZEHR.Security.Crypto;

namespace EZEHR.Tests.Records;

public class RecordStoreTests
{
    private sealed class CountingWrapper(IKeyWrapper inner) : IKeyWrapper
    {
        public int Unwraps;
        public Task<byte[]> WrapAsync(byte[] dek) => inner.WrapAsync(dek);
        public Task<byte[]> UnwrapAsync(byte[] wrapped) { Unwraps++; return inner.UnwrapAsync(wrapped); }
    }

    private readonly Microsoft.Data.Sqlite.SqliteConnection db = Db.Open(":memory:");
    private readonly CountingWrapper wrapper = new(new LocalKeyWrapper(RandomNumberGenerator.GetBytes(32)));
    private RecordStore Store => new(db, wrapper);

    private const string Jws = "eyJhbGciOiJFUzI1NiJ9.eyJyZXNvdXJjZVR5cGUiOiJPYnNlcnZhdGlvbiJ9.c2ln";

    [Fact] // Review Focus 4
    public async Task Put_is_idempotent()
    {
        var patient = await Store.CreatePatientAsync("Sam Taylor");
        var first = await Store.PutAsync(patient, "urn:provider:maple|Observation/egfr-1", "Observation", Jws);
        var second = await Store.PutAsync(patient, "urn:provider:maple|Observation/egfr-1", "Observation", Jws);
        Assert.True(first.Inserted);
        Assert.False(second.Inserted);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM entries"));
    }

    [Fact]
    public async Task Ciphertext_has_no_plaintext()
    {
        var patient = await Store.CreatePatientAsync("Sam Taylor");
        await Store.PutAsync(patient, "urn:provider:maple|Observation/egfr-1", "Observation", Jws);
        var raw = db.QuerySingle<byte[]>("SELECT ciphertext FROM entries");
        var text = Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("resourceType", text);
        Assert.DoesNotContain("eyJ", text);
        var wrappedDek = db.QuerySingle<byte[]>("SELECT wrapped_dek FROM patients");
        Assert.True(wrappedDek.Length > 32);
    }

    [Fact]
    public async Task Read_filters_by_type()
    {
        var patient = await Store.CreatePatientAsync("Sam Taylor");
        await Store.PutAsync(patient, "a|Observation/1", "Observation", Jws + "1");
        await Store.PutAsync(patient, "a|Condition/1", "Condition", Jws + "2");
        var other = await Store.CreatePatientAsync("Someone Else");
        await Store.PutAsync(other, "a|Observation/1", "Observation", Jws + "3");

        var all = await Store.ReadAsync(patient, null);
        Assert.Equal(2, all.Count);
        var labs = await Store.ReadAsync(patient, t => t == "Observation");
        Assert.Equal(Jws + "1", Assert.Single(labs).Jws);
    }

    [Fact]
    public async Task Read_unwraps_key_every_call()
    {
        var patient = await Store.CreatePatientAsync("Sam Taylor");
        await Store.PutAsync(patient, "a|Observation/1", "Observation", Jws);
        var before = wrapper.Unwraps;
        await Store.ReadAsync(patient, null);
        await Store.ReadAsync(patient, null);
        Assert.Equal(before + 2, wrapper.Unwraps);
    }

    [Fact]
    public async Task Entry_cannot_be_moved_to_another_patient()
    {
        var a = await Store.CreatePatientAsync("A");
        var b = await Store.CreatePatientAsync("B");
        await Store.PutAsync(a, "a|Observation/1", "Observation", Jws);
        db.Execute("UPDATE entries SET patient_id = @b", new { b = b.ToString() });
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store.ReadAsync(b, null));
    }

    [Fact]
    public async Task Unknown_patient_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.PutAsync(Guid.NewGuid(), "k", "Observation", Jws));
    }
}
