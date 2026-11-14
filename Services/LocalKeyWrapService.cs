using System.Security.Cryptography;
using EZEHR.Models;
using Microsoft.Extensions.Options;

namespace EZEHR.Services;

/// <summary>Development and tests only: wraps keys with AES-256-GCM under a local key. Output is iv(12) || ciphertext || tag(16).</summary>
public sealed class LocalKeyWrapService(IOptions<AppOptions> options, IHostEnvironment environment)
    : IKeyWrapService
{
    private const string Aad = "ezehr-local-kek";

    private readonly byte[] _kek = LoadKek(options.Value, environment);

    public Task<byte[]> WrapAsync(byte[] dek)
    {
        (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(_kek, Aad, dek);
        return Task.FromResult(iv.Concat(ciphertext).ToArray());
    }

    public Task<byte[]> UnwrapAsync(byte[] wrapped)
    {
        if (wrapped.Length < 12 + 16)
        {
            throw new CryptographicException("Wrapped key is too short.");
        }
        return Task.FromResult(RecordCipher.DecryptBytes(_kek, Aad, wrapped[..12], wrapped[12..]));
    }

    private static byte[] LoadKek(AppOptions options, IHostEnvironment environment)
    {
        byte[] kek = ReadOrCreateKek(options, environment);
        if (kek.Length != 32)
        {
            throw new InvalidOperationException("The key-encryption key must be 32 bytes.");
        }
        return kek;
    }

    private static byte[] ReadOrCreateKek(AppOptions options, IHostEnvironment environment)
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
        byte[] kek = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, kek);
        return kek;
    }
}
