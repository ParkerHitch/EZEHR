using EZEHR.Models;
using Fido2NetLib;

namespace EZEHR.Services;

/// <summary>
/// WebAuthn ceremonies for patient passkeys (fido2-net-lib). Verification failures come back as null, never as
/// exceptions: a rejected passkey is an expected outcome.
/// </summary>
public interface IFido2Service
{
    /// <summary>Options for a discoverable passkey with user verification required and no attestation.</summary>
    CredentialCreateOptions CreateRegistrationOptions(Fido2User user);

    Task<RegisteredPasskey?> VerifyRegistrationAsync(
        AuthenticatorAttestationRawResponse attestation,
        CredentialCreateOptions originalOptions,
        CancellationToken cancellationToken = default
    );

    /// <summary>Usernameless options: any passkey for this site may answer, with user verification required.</summary>
    AssertionOptions CreateSignInOptions();

    /// <summary>The new signature counter when the assertion is valid for the stored passkey, otherwise null.</summary>
    Task<uint?> VerifySignInAsync(
        AuthenticatorAssertionRawResponse assertion,
        AssertionOptions originalOptions,
        StoredPasskey storedPasskey,
        CancellationToken cancellationToken = default
    );
}
