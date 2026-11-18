using EZEHR.Models;
using EZEHR.Services;

namespace EZEHR.Tools.Calls;

/// <summary>One entry as a reader checked it: verified, or why not.</summary>
public sealed record CheckedEntry(SignedEntry Entry, VerifiedEntry? Verified, string? Failure);

/// <summary>
/// What every reader on the network does with what it gets: checks each signature itself, against its own copy of
/// the network's trust anchors (.pki/anchors.pem and intermediates.pem), never trusting the server's word.
/// </summary>
public static class SignatureCheck
{
    public static IReadOnlyList<CheckedEntry> Verify(IReadOnlyList<SignedEntry> entries)
    {
        TrustAnchors trustAnchors = CertificateValidator.LoadTrustAnchors(
            new AppOptions
            {
                TrustAnchorsPemPath = ToolPaths.Pki("anchors.pem"),
                IntermediatesPemPath = ToolPaths.Pki("intermediates.pem"),
            }
        );
        OrganizationDirectory directory = ToolOrganizations.Load();
        List<CheckedEntry> checkedEntries = [];
        foreach (SignedEntry entry in entries)
        {
            try
            {
                VerifiedEntry verified = RecordVerifier.Verify(
                    entry.Id,
                    entry.Jws,
                    trustAnchors,
                    directory
                );
                checkedEntries.Add(new CheckedEntry(entry, verified, null));
            }
            catch (RecordSignatureException exception)
            {
                checkedEntries.Add(new CheckedEntry(entry, null, exception.Message));
            }
        }
        return checkedEntries;
    }
}
