using System.Text.Json;

namespace EZEHR.Domain;

/// <summary>A record entry whose signature and signer certificate chain have been checked.</summary>
public sealed record VerifiedEntry(
    Guid Id,
    string ResourceType,
    JsonElement Resource,
    Party Signer,
    DateTimeOffset SignedAt,
    string? WrittenBy);
