using System.Text.Json;

namespace EZEHR.Models;

/// <summary>A record entry whose signature and signer certificate chain have been checked.</summary>
public sealed record VerifiedEntry(
    Guid Id,
    string ResourceType,
    JsonElement Resource,
    Party Signer,
    DateTimeOffset SignedAt,
    string? WrittenBy
);

/// <summary>A verified entry with its original JWS, so others can verify it too. This is what the network API returns.</summary>
public sealed record SignedEntry(
    Guid Id,
    string ResourceType,
    JsonElement Resource,
    Party Signer,
    DateTimeOffset SignedAt,
    string? WrittenBy,
    string Jws
);

/// <summary>A decrypted entry, not yet verified.</summary>
public sealed record StoredEntry(Guid Id, string ResourceType, string Jws);
