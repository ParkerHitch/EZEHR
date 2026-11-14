using System.Security.Cryptography;
using System.Text;

namespace EZEHR.Security.Crypto;

/// <summary>
/// AES-256-GCM with a fresh 12-byte IV and the 16-byte tag appended to the ciphertext.
/// The associated data binds a ciphertext to its place ({patientId}:{entryId}), so rows can't be swapped between patients.
/// </summary>
public static class RecordCipher
{
    private const int TagSize = 16;

    public static (byte[] Iv, byte[] Ciphertext) Encrypt(byte[] dek, string aad, string plaintext) =>
        Encrypt(dek, aad, Encoding.UTF8.GetBytes(plaintext));

    public static (byte[] Iv, byte[] Ciphertext) Encrypt(byte[] dek, string aad, byte[] plaintext)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var output = new byte[plaintext.Length + TagSize];
        using var aes = new AesGcm(dek, TagSize);
        aes.Encrypt(iv, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), Encoding.UTF8.GetBytes(aad));
        return (iv, output);
    }

    public static string Decrypt(byte[] dek, string aad, byte[] iv, byte[] ciphertext) =>
        Encoding.UTF8.GetString(DecryptBytes(dek, aad, iv, ciphertext));

    public static byte[] DecryptBytes(byte[] dek, string aad, byte[] iv, byte[] ciphertext)
    {
        if (ciphertext.Length < TagSize) throw new CryptographicException("Ciphertext is too short.");
        var length = ciphertext.Length - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(dek, TagSize);
        aes.Decrypt(iv, ciphertext.AsSpan(0, length), ciphertext.AsSpan(length), plaintext, Encoding.UTF8.GetBytes(aad));
        return plaintext;
    }
}
