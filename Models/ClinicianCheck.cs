namespace EZEHR.Models;

/// <summary>
/// Whether a signed-in account may act as a clinician. Party and SessionStartedAt are set when Ok; ErrorCopy is
/// the sentence to show when not.
/// </summary>
public sealed record ClinicianCheck(
    bool Ok,
    Party? Party,
    DateTimeOffset? SessionStartedAt,
    string? ErrorCopy
);
