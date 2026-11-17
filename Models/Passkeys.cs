using Fido2NetLib;

namespace EZEHR.Models;

/// <summary>A passkey the phone just created, as Fido2 verified it. Only the public key is ever stored.</summary>
public sealed record RegisteredPasskey(
    byte[] CredentialId,
    byte[] PublicKey,
    uint SignCount,
    byte[] UserHandle
);

/// <summary>A patient's passkey as stored at enrollment, used to check a sign-in.</summary>
public sealed record StoredPasskey(
    byte[] CredentialId,
    Guid PatientId,
    byte[] PublicKey,
    uint SignCount,
    byte[] UserHandle
);

/// <summary>Why an enrollment, invite link or passkey sign-in failed.</summary>
public enum EnrollmentError
{
    InviteUnknown,
    InviteUsed,
    InviteExpired,
    InviteLinkedElsewhere,
    PasskeyRejected,
    PasskeyUnknown,
}

/// <summary>PatientId is set when it succeeded (the patient to sign in), Error when it didn't.</summary>
public sealed record EnrollmentResult(bool Succeeded, Guid? PatientId, EnrollmentError? Error);

public sealed record CreateRegistrationOptionsResult(
    bool Succeeded,
    CredentialCreateOptions? Options,
    EnrollmentError? Error
);

/// <summary>
/// An invite looked at without being used, so the page can name the provider that sent it. Apps is every app a new
/// patient can turn on while enrolling, all off; empty when the invite can't be used.
/// </summary>
public sealed record InviteCheck(
    bool Usable,
    string? ProviderName,
    EnrollmentError? Error,
    IReadOnlyList<AppLine> Apps
);
