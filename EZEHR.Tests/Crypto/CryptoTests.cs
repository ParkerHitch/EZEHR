using System.Security.Cryptography;
using EZEHR.Security.Crypto;

namespace EZEHR.Tests.Crypto;

public class CryptoTests
{
    private static readonly byte[] Dek = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Cipher_roundtrip()
    {
        var (iv, ct) = RecordCipher.Encrypt(Dek, "p:e", "hello record");
        Assert.Equal(12, iv.Length);
        Assert.Equal("hello record".Length + 16, ct.Length);
        Assert.Equal("hello record", RecordCipher.Decrypt(Dek, "p:e", iv, ct));
    }

    [Fact]
    public void Cipher_wrong_aad_throws()
    {
        var (iv, ct) = RecordCipher.Encrypt(Dek, "patientA:entry1", "hello record");
        Assert.ThrowsAny<CryptographicException>(() => RecordCipher.Decrypt(Dek, "patientB:entry1", iv, ct));
    }

    [Fact]
    public void Cipher_tamper_throws()
    {
        var (iv, ct) = RecordCipher.Encrypt(Dek, "p:e", "hello record");
        ct[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => RecordCipher.Decrypt(Dek, "p:e", iv, ct));
    }

    [Fact]
    public void Cipher_uses_fresh_iv()
    {
        var a = RecordCipher.Encrypt(Dek, "p:e", "same");
        var b = RecordCipher.Encrypt(Dek, "p:e", "same");
        Assert.NotEqual(a.Iv, b.Iv);
    }

    [Fact]
    public async Task LocalWrapper_roundtrip()
    {
        var wrapper = new LocalKeyWrapper(RandomNumberGenerator.GetBytes(32));
        var wrapped = await wrapper.WrapAsync(Dek);
        Assert.NotEqual(Dek, wrapped);
        Assert.Equal(Dek, await wrapper.UnwrapAsync(wrapped));

        var otherKek = new LocalKeyWrapper(RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => otherKek.UnwrapAsync(wrapped));
    }
}
