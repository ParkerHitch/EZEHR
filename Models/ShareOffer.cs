namespace EZEHR.Models;

/// <summary>An opaque, single-use share code (8 characters; GrantManager.FormatCode shows it as XXXX-XXXX). It carries no data or keys.</summary>
public sealed record ShareOffer(
    Guid Id,
    Guid PatientId,
    string Code,
    ShareDuration Duration,
    DateTimeOffset OfferExpiresAt,
    Guid? RedeemedGrantId
);
