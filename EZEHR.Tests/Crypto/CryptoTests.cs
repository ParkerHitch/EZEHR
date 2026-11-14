using System.Security.Cryptography;
using EZEHR.Services;

namespace EZEHR.Tests.Crypto;

public class CryptoTests
{
    private static readonly byte[] DataKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Cipher_roundtrip()
    {
        (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(DataKey, "p:e", "hello record");
        Assert.Equal(12, iv.Length);
        Assert.Equal("hello record".Length + 16, ciphertext.Length);
        Assert.Equal("hello record", RecordCipher.Decrypt(DataKey, "p:e", iv, ciphertext));
    }

    [Fact]
    public void Cipher_wrong_aad_throws()
    {
        (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(
            DataKey,
            "patientA:entry1",
            "hello record"
        );
        Assert.ThrowsAny<CryptographicException>(() =>
            RecordCipher.Decrypt(DataKey, "patientB:entry1", iv, ciphertext)
        );
    }

    [Fact]
    public void Cipher_tamper_throws()
    {
        (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(DataKey, "p:e", "hello record");
        ciphertext[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() =>
            RecordCipher.Decrypt(DataKey, "p:e", iv, ciphertext)
        );
    }

    [Fact]
    public void Cipher_uses_fresh_iv()
    {
        (byte[] firstIv, byte[] _) = RecordCipher.Encrypt(DataKey, "p:e", "same");
        (byte[] secondIv, byte[] _) = RecordCipher.Encrypt(DataKey, "p:e", "same");
        Assert.NotEqual(firstIv, secondIv);
    }

    [Fact]
    public async Task LocalKeyWrap_roundtrip()
    {
        LocalKeyWrapService keyWrap = TestServices.LocalKeyWrapService();
        byte[] wrappedKey = await keyWrap.WrapAsync(DataKey);
        Assert.NotEqual(DataKey, wrappedKey);
        Assert.Equal(DataKey, await keyWrap.UnwrapAsync(wrappedKey));

        LocalKeyWrapService otherKeyEncryptionKey = TestServices.LocalKeyWrapService();
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            otherKeyEncryptionKey.UnwrapAsync(wrappedKey)
        );
    }
}
