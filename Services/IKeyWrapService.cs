namespace EZEHR.Services;

/// <summary>Wraps and unwraps a patient's data-encryption key with a key-encryption key that never leaves its keystore.</summary>
public interface IKeyWrapService
{
    Task<byte[]> WrapAsync(byte[] dek);

    Task<byte[]> UnwrapAsync(byte[] wrapped);
}
