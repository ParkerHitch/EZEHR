using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EZEHR.Models;

namespace EZEHR.Services;

/// <summary>
/// What checking a patient's audit log found. UnchainedCount is how many of the oldest events were written before
/// fingerprints were kept, so nothing vouches for them. HeadHash is the newest event's fingerprint: it covers the
/// whole chain before it. FirstBrokenId is the first event that doesn't match, when the log isn't intact.
/// </summary>
public sealed record AuditChainCheck(
    bool Intact,
    int EventCount,
    int UnchainedCount,
    string? HeadHash,
    long? FirstBrokenId
);

/// <summary>
/// The audit log's hash chain. Each event's fingerprint is the SHA-256 of the event's contents together with the
/// fingerprint of the patient's previous event, so changing, removing or inserting an event breaks every
/// fingerprint after it. One chain per patient, in id order.
/// </summary>
public static class AuditChain
{
    /// <summary>The fingerprint of one event, as lowercase hex. <paramref name="previousHash"/> is null for the first.</summary>
    public static string ComputeHash(
        string? previousHash,
        Guid patientId,
        DateTimeOffset at,
        Party? actor,
        string action,
        string? claimedPurpose,
        bool granted,
        DenyReason? reason,
        string? encounterId = null
    )
    {
        // A JSON array, so no value can run into the next one and two different events can't hash the same text.
        // The encounter is a thirteenth element only when there is one, so events from before encounters were
        // recorded keep the fingerprints they already have.
        List<object?> values =
        [
            previousHash,
            patientId.ToString("D"),
            at.UtcTicks,
            actor?.Kind.ToString(),
            actor?.SubjectUri,
            actor?.DisplayName,
            actor?.OrgName,
            actor?.OrgUri,
            action,
            claimedPurpose,
            granted,
            reason?.ToString(),
        ];
        if (encounterId is not null)
        {
            values.Add(encounterId);
        }
        string canonical = JsonSerializer.Serialize(values);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Recomputes every fingerprint of one patient's events, oldest first. Events from before fingerprints were kept
    /// can only come first; one without a fingerprint anywhere later counts as a break.
    /// </summary>
    public static AuditChainCheck Verify(IReadOnlyList<AuditEvent> eventsOldestFirst)
    {
        string? previousHash = null;
        int unchainedCount = 0;
        bool chainStarted = false;
        foreach (AuditEvent auditEvent in eventsOldestFirst)
        {
            if (auditEvent.ChainHash is null && !chainStarted)
            {
                unchainedCount++;
                continue;
            }
            chainStarted = true;
            string expectedHash = ComputeHash(
                previousHash,
                auditEvent.PatientId,
                auditEvent.At,
                auditEvent.Actor,
                auditEvent.Action,
                auditEvent.ClaimedPurpose,
                auditEvent.Granted,
                auditEvent.Reason,
                auditEvent.EncounterId
            );
            if (auditEvent.ChainHash != expectedHash)
            {
                return new AuditChainCheck(
                    false,
                    eventsOldestFirst.Count,
                    unchainedCount,
                    previousHash,
                    auditEvent.Id
                );
            }
            previousHash = auditEvent.ChainHash;
        }
        return new AuditChainCheck(
            true,
            eventsOldestFirst.Count,
            unchainedCount,
            previousHash,
            null
        );
    }

    /// <summary>The first 16 hex digits in groups of four: short enough to write down or read aloud.</summary>
    public static string Fingerprint(string hash)
    {
        return string.Join(
            ' ',
            Enumerable.Range(0, 4).Select(group => hash.Substring(group * 4, 4))
        );
    }
}
