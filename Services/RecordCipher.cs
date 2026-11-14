using System.Security.Cryptography;
using System.Text;

namespace EZEHR.Services;

/// <summary>
/// AES-256-GCM with a fresh 12-byte IV and the 16-byte tag appended to the ciphertext.
/// The associated data binds a ciphertext to its place ({patientId}:{entryId}), so rows can't be swapped between patients.
/// </summary>
public static class RecordCipher
{
    private const int TagSize = 16;

    public static (byte[] Iv, byte[] Ciphertext) Encrypt(
        byte[] dataKey,
        string associatedData,
        string plaintext
    )
    {
        return Encrypt(dataKey, associatedData, Encoding.UTF8.GetBytes(plaintext));
    }

    public static (byte[] Iv, byte[] Ciphertext) Encrypt(
        byte[] dataKey,
        string associatedData,
        byte[] plaintext
    )
    {
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] output = new byte[plaintext.Length + TagSize];
        using AesGcm aes = new(dataKey, TagSize);
        aes.Encrypt(
            iv,
            plaintext,
            output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length),
            Encoding.UTF8.GetBytes(associatedData)
        );
        return (iv, output);
    }

    public static string Decrypt(
        byte[] dataKey,
        string associatedData,
        byte[] iv,
        byte[] ciphertext
    )
    {
        return Encoding.UTF8.GetString(DecryptBytes(dataKey, associatedData, iv, ciphertext));
    }

    public static byte[] DecryptBytes(
        byte[] dataKey,
        string associatedData,
        byte[] iv,
        byte[] ciphertext
    )
    {
        if (ciphertext.Length < TagSize)
        {
            throw new CryptographicException("Ciphertext is too short.");
        }
        int length = ciphertext.Length - TagSize;
        byte[] plaintext = new byte[length];
        using AesGcm aes = new(dataKey, TagSize);
        aes.Decrypt(
            iv,
            ciphertext.AsSpan(0, length),
            ciphertext.AsSpan(length),
            plaintext,
            Encoding.UTF8.GetBytes(associatedData)
        );
        return plaintext;
    }
}
