namespace EZEHR.Security.Crypto;

/// <summary>Wraps and unwraps a patient's data-encryption key with a key-encryption key that never leaves its keystore.</summary>
public interface IKeyWrapper
{
    Task<byte[]> WrapAsync(byte[] dek);
    Task<byte[]> UnwrapAsync(byte[] wrapped);
}
