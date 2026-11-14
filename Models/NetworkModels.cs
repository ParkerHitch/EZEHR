using System.ComponentModel.DataAnnotations;

namespace EZEHR.Models;

public sealed record CreateEnrollmentInviteRequest([Required] string ProviderPatientId);

public sealed record PushSignedRecordsRequest(
    [Required] string ProviderPatientId,
    [Required] string[] SignedEntries
);

public sealed record WriteSignedNoteRequest([Required] string Jws);

public sealed record CreateEnrollmentInviteResponse(string InviteCode, string EnrollmentUrl);

public sealed record PushSignedRecordsResponse(int Stored, int AlreadyStored);

public sealed record WriteSignedNoteResponse(bool Stored, Guid? EntryId);

public sealed record SignedEntriesResponse(IReadOnlyList<SignedEntry> Entries);

public sealed record ErrorResponse(string Error);
