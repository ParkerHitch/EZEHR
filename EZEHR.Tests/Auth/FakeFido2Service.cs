using EZEHR.Models;
using EZEHR.Services;
using Fido2NetLib;
using Microsoft.Extensions.Logging.Abstractions;

namespace EZEHR.Tests.Auth;

/// <summary>
/// Real Fido2 options (so the JSON the browser would get is real), stubbed verification: there is no
/// authenticator in a test. Tests choose what the "phone" proves by setting the next results.
/// </summary>
public sealed class FakeFido2Service : IFido2Service
{
    private readonly Fido2Service _options = new(
        new Fido2(
            new Fido2Configuration
            {
                ServerDomain = "ezehr.test",
                ServerName = "EZEHR",
                Origins = new HashSet<string> { "https://ezehr.test" },
            }
        ),
        NullLogger<Fido2Service>.Instance
    );

    /// <summary>What the next registration verifies to; null means the attestation is rejected.</summary>
    public RegisteredPasskey? NextRegistration { get; set; }

    /// <summary>The new signature counter the next sign-in verifies to; null means the assertion is rejected.</summary>
    public uint? NextSignInCounter { get; set; }

    /// <summary>The passkey the last sign-in was checked against.</summary>
    public StoredPasskey? LastCheckedPasskey { get; private set; }

    public static RegisteredPasskey NewPasskey(CredentialCreateOptions options)
    {
        return NewPasskey(options.User.Id);
    }

    public static RegisteredPasskey NewPasskey(byte[] userHandle)
    {
        return new RegisteredPasskey(Guid.NewGuid().ToByteArray(), [1, 2, 3], 0, userHandle);
    }

    public CredentialCreateOptions CreateRegistrationOptions(Fido2User user)
    {
        return _options.CreateRegistrationOptions(user);
    }

    public Task<RegisteredPasskey?> VerifyRegistrationAsync(
        AuthenticatorAttestationRawResponse attestation,
        CredentialCreateOptions originalOptions,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(NextRegistration);
    }

    public AssertionOptions CreateSignInOptions()
    {
        return _options.CreateSignInOptions();
    }

    public Task<uint?> VerifySignInAsync(
        AuthenticatorAssertionRawResponse assertion,
        AssertionOptions originalOptions,
        StoredPasskey storedPasskey,
        CancellationToken cancellationToken = default
    )
    {
        LastCheckedPasskey = storedPasskey;
        return Task.FromResult(NextSignInCounter);
    }
}
