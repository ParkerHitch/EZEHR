using System.ComponentModel.DataAnnotations;
using EZEHR.Services;

namespace EZEHR.Models;

public sealed record CreateEnrollmentInviteRequest([Required] string ProviderPatientId);

public sealed record PushSignedRecordsRequest(
    [Required] string ProviderPatientId,
    [Required] string[] SignedEntries
);

public sealed record WriteSignedNoteRequest([Required] string Jws);

/// <summary>The slug becomes urn:provider:{slug} and the certificate CN {slug}.providers.ezehr.</summary>
public sealed record RegisterProviderRequest(
    [Required, RegularExpression(OrgDirectory.SlugPattern)] string Slug,
    [Required, StringLength(200, MinimumLength = 1)] string DisplayName
);

public sealed record RegisterProviderResponse(string Uri, string CertificateCommonName);

public sealed record CreateEnrollmentInviteResponse(string InviteCode, string EnrollmentUrl);

/// <summary>Replaced counts records the provider corrected: same resource id, changed content.</summary>
public sealed record PushSignedRecordsResponse(int Stored, int AlreadyStored, int Replaced);

public sealed record WriteSignedNoteResponse(bool Stored, Guid? EntryId);

public sealed record SignedEntriesResponse(IReadOnlyList<SignedEntry> Entries);

public sealed record ErrorResponse(string Error);

public sealed record ReadOrganizationAuditResponse(
    IReadOnlyList<OrganizationAuditEvent> Events,
    long? NextAfterId
);

public sealed record ListOrganizationCliniciansResponse(
    IReadOnlyList<OrganizationClinician> Clinicians
);
