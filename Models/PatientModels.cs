using System.ComponentModel.DataAnnotations;
using Fido2NetLib;

namespace EZEHR.Models;

public sealed record CreateRegistrationOptionsRequest(
    [Required] string InviteCode,
    [Required] string DisplayName
);

/// <summary>AppUris is the apps the patient ticked on the enrollment page. None is fine.</summary>
public sealed record CompleteRegistrationRequest(
    [Required] AuthenticatorAttestationRawResponse Attestation,
    string[]? AppUris
);

public sealed record CompleteSignInRequest([Required] AuthenticatorAssertionRawResponse Assertion);

/// <summary>Where the browser goes once a passkey ceremony succeeds.</summary>
public sealed record PasskeyCompletedResponse(string RedirectTo);
