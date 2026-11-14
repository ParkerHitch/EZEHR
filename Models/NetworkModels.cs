using System.ComponentModel.DataAnnotations;

namespace EZEHR.Models;

public sealed record InviteRequest([Required] string ProviderPatientId);

public sealed record IngestRequest(
    [Required] string ProviderPatientId,
    [Required] string[] Entries
);

public sealed record NoteRequest([Required] string Jws);

public sealed record InviteResponse(string Code, string Url);

public sealed record IngestResponse(int Inserted, int Skipped);

public sealed record NoteResponse(bool Inserted, Guid? Id);

public sealed record EntriesResponse(IReadOnlyList<SignedEntry> Entries);

public sealed record ErrorResponse(string Error);
