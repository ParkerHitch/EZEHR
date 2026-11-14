using System.ComponentModel.DataAnnotations;
using Fido2NetLib;

namespace EZEHR.Models;

public sealed record CreateRegistrationOptionsRequest(
    [Required] string InviteCode,
    [Required] string DisplayName
);

public sealed record CompleteRegistrationRequest(
    [Required] AuthenticatorAttestationRawResponse Attestation,
    bool KidneyCheckOptIn
);

public sealed record CompleteSignInRequest([Required] AuthenticatorAssertionRawResponse Assertion);

/// <summary>Where the browser goes once a passkey ceremony succeeds.</summary>
public sealed record PasskeyCompletedResponse(string RedirectTo);
