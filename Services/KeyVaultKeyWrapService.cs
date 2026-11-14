using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using EZEHR.Models;
using Microsoft.Extensions.Options;

namespace EZEHR.Services;

/// <summary>
/// Wraps keys with the Key Vault RSA key (records-kek) using RSA-OAEP-256. Authenticates with the App Service
/// managed identity (DefaultAzureCredential), so no secret is configured anywhere. Every unwrap is a Key Vault call.
/// </summary>
public sealed class KeyVaultKeyWrapService(IOptions<AppOptions> options) : IKeyWrapService
{
    private readonly CryptographyClient _client = new(
        new Uri(
            options.Value.KeyVault.KeyId
                ?? throw new InvalidOperationException(
                    "App:KeyVault:KeyId is required when App:KeyWrapper:Mode is KeyVault."
                )
        ),
        new DefaultAzureCredential()
    );

    public async Task<byte[]> WrapAsync(byte[] dataKey)
    {
        WrapResult result = await _client.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, dataKey);
        return result.EncryptedKey;
    }

    public async Task<byte[]> UnwrapAsync(byte[] wrappedKey)
    {
        UnwrapResult result = await _client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrappedKey);
        return result.Key;
    }
}
