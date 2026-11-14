using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace EZEHR.Security.Crypto;

/// <summary>
/// Wraps keys with the Key Vault RSA key (records-kek) using RSA-OAEP-256. Authenticates with the App Service
/// managed identity (DefaultAzureCredential), so no secret is configured anywhere. Every unwrap is a Key Vault call.
/// </summary>
public sealed class KeyVaultKeyWrapper(CryptographyClient client) : IKeyWrapper
{
    public KeyVaultKeyWrapper(Uri keyId, TokenCredential? credential = null)
        : this(new CryptographyClient(keyId, credential ?? new DefaultAzureCredential())) { }

    public async Task<byte[]> WrapAsync(byte[] dek) =>
        (await client.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, dek)).EncryptedKey;

    public async Task<byte[]> UnwrapAsync(byte[] wrapped) =>
        (await client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, wrapped)).Key;
}
