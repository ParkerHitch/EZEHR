using EZEHR.Models;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace EZEHR.Services;

public sealed class Fido2Service(IFido2 fido2, ILogger<Fido2Service> logger) : IFido2Service
{
    public CredentialCreateOptions CreateRegistrationOptions(Fido2User user)
    {
        return fido2.RequestNewCredential(
            new RequestNewCredentialParams
            {
                User = user,
                ExcludeCredentials = [],
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Required,
                    UserVerification = UserVerificationRequirement.Required,
                },
                AttestationPreference = AttestationConveyancePreference.None,
                PubKeyCredParams = PubKeyCredParam.Defaults,
            }
        );
    }

    public async Task<RegisteredPasskey?> VerifyRegistrationAsync(
        AuthenticatorAttestationRawResponse attestation,
        CredentialCreateOptions originalOptions,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            RegisteredPublicKeyCredential credential = await fido2.MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = attestation,
                    OriginalOptions = originalOptions,
                    // The credential_id primary key is what keeps credential ids unique.
                    IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true),
                },
                cancellationToken
            );
            return new RegisteredPasskey(
                credential.Id,
                credential.PublicKey,
                credential.SignCount,
                originalOptions.User.Id
            );
        }
        catch (Fido2VerificationException exception)
        {
            logger.LogWarning(exception, "Passkey registration was rejected.");
            return null;
        }
    }

    public AssertionOptions CreateSignInOptions()
    {
        return fido2.GetAssertionOptions(
            new GetAssertionOptionsParams
            {
                AllowedCredentials = [],
                UserVerification = UserVerificationRequirement.Required,
            }
        );
    }

    public async Task<uint?> VerifySignInAsync(
        AuthenticatorAssertionRawResponse assertion,
        AssertionOptions originalOptions,
        StoredPasskey storedPasskey,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            VerifyAssertionResult result = await fido2.MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = assertion,
                    OriginalOptions = originalOptions,
                    StoredPublicKey = storedPasskey.PublicKey,
                    StoredSignatureCounter = storedPasskey.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (owner, _) =>
                        Task.FromResult(owner.UserHandle.SequenceEqual(storedPasskey.UserHandle)),
                },
                cancellationToken
            );
            return result.SignCount;
        }
        catch (Fido2VerificationException exception)
        {
            logger.LogWarning(exception, "Passkey sign-in was rejected.");
            return null;
        }
    }
}
