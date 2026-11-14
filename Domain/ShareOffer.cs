namespace EZEHR.Domain;

/// <summary>An opaque, single-use share code (8 characters, shown XXXX-XXXX). It carries no data or keys.</summary>
public sealed record ShareOffer(
    Guid Id,
    Guid PatientId,
    string Code,
    ShareDuration Duration,
    DateTimeOffset OfferExpiresAt,
    Guid? RedeemedGrantId)
{
    public string DisplayCode => Code.Length == 8 ? $"{Code[..4]}-{Code[4..]}" : Code;
}
