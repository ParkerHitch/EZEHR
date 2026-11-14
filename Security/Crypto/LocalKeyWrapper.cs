using System.Security.Cryptography;

namespace EZEHR.Security.Crypto;

/// <summary>Development and tests only: wraps keys with AES-256-GCM under a local key. Output is iv(12) || ciphertext || tag(16).</summary>
public sealed class LocalKeyWrapper : IKeyWrapper
{
    private const string Aad = "ezehr-local-kek";
    private readonly byte[] kek;

    public LocalKeyWrapper(byte[] kek)
    {
        if (kek.Length != 32) throw new ArgumentException("The key-encryption key must be 32 bytes.", nameof(kek));
        this.kek = kek;
    }

    public Task<byte[]> WrapAsync(byte[] dek)
    {
        var (iv, ct) = RecordCipher.Encrypt(kek, Aad, dek);
        return Task.FromResult(iv.Concat(ct).ToArray());
    }

    public Task<byte[]> UnwrapAsync(byte[] wrapped)
    {
        if (wrapped.Length < 12 + 16) throw new CryptographicException("Wrapped key is too short.");
        return Task.FromResult(RecordCipher.DecryptBytes(kek, Aad, wrapped[..12], wrapped[12..]));
    }
}
