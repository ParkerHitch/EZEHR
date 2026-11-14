using System.Security.Cryptography;
using EZEHR.Models;
using Microsoft.Extensions.Options;

namespace EZEHR.Services;

/// <summary>Development and tests only: wraps keys with AES-256-GCM under a local key. Output is iv(12) || ciphertext || tag(16).</summary>
public sealed class LocalKeyWrapService(IOptions<AppOptions> options, IHostEnvironment environment)
    : IKeyWrapService
{
    private const string AssociatedData = "ezehr-local-kek";

    private readonly byte[] _keyEncryptionKey = LoadKeyEncryptionKey(options.Value, environment);

    public Task<byte[]> WrapAsync(byte[] dataKey)
    {
        (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(
            _keyEncryptionKey,
            AssociatedData,
            dataKey
        );
        return Task.FromResult(iv.Concat(ciphertext).ToArray());
    }

    public Task<byte[]> UnwrapAsync(byte[] wrappedKey)
    {
        if (wrappedKey.Length < 12 + 16)
        {
            throw new CryptographicException("Wrapped key is too short.");
        }
        return Task.FromResult(
            RecordCipher.DecryptBytes(
                _keyEncryptionKey,
                AssociatedData,
                wrappedKey[..12],
                wrappedKey[12..]
            )
        );
    }

    private static byte[] LoadKeyEncryptionKey(AppOptions options, IHostEnvironment environment)
    {
        byte[] keyEncryptionKey = ReadOrCreateKeyEncryptionKey(options, environment);
        if (keyEncryptionKey.Length != 32)
        {
            throw new InvalidOperationException("The key-encryption key must be 32 bytes.");
        }
        return keyEncryptionKey;
    }

    private static byte[] ReadOrCreateKeyEncryptionKey(
        AppOptions options,
        IHostEnvironment environment
    )
    {
        if (!string.IsNullOrEmpty(options.LocalKekBase64))
        {
            return Convert.FromBase64String(options.LocalKekBase64);
        }
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "App:LocalKekBase64 is required for App:KeyWrapper:Mode=Local outside Development. Use KeyVault in production."
            );
        }

        // Development convenience: a random key kept in the git-ignored .pki folder, so the dev database survives restarts.
        string path = Path.Combine(environment.ContentRootPath, ".pki", "dev-kek.bin");
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] keyEncryptionKey = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, keyEncryptionKey);
        return keyEncryptionKey;
    }
}
