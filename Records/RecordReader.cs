using System.Text.Json;
using EZEHR.Access;
using EZEHR.Domain;
using EZEHR.Security.Pki;

namespace EZEHR.Records;

/// <summary>A record entry whose signature checked out, with the original JWS so others can verify it too.</summary>
public sealed record SignedEntry(VerifiedEntry Verified, string Jws);

/// <summary>Reads and verifies a patient's record. Other parties go through the gate; the patient's own pages use ReadOwnAsync.</summary>
public sealed class RecordReader(RecordStore store, RecordVerifier verifier, AccessGate gate, ILogger<RecordReader> log)
{
    public const string EgfrLoinc = "98979-8";

    /// <summary>Asks the gate (one audit event), then returns the verified entries the action allows.</summary>
    public async Task<(AccessDecision Decision, IReadOnlyList<SignedEntry> Entries)> ReadAsync(
        Guid patientId, AccessAction action, Caller caller, string? claimedPurpose)
    {
        var decision = gate.Authorize(patientId, action, caller, claimedPurpose);
        if (!decision.Allowed) return (decision, []);
        if (action == AccessAction.ReadLabs)
            return (decision, (await VerifiedAsync(patientId, t => t == "Observation")).Where(e => HasLoinc(e.Verified.Resource, EgfrLoinc)).ToList());
        return (decision, await VerifiedAsync(patientId, null));
    }

    /// <summary>For the signed-in patient's own pages only.</summary>
    public Task<IReadOnlyList<SignedEntry>> ReadOwnAsync(Guid patientId) => VerifiedAsync(patientId, null);

    private async Task<IReadOnlyList<SignedEntry>> VerifiedAsync(Guid patientId, Func<string, bool>? filter)
    {
        var result = new List<SignedEntry>();
        foreach (var stored in await store.ReadAsync(patientId, filter))
        {
            try { result.Add(new SignedEntry(verifier.Verify(stored.Id, stored.Jws), stored.Jws)); }
            catch (RecordSignatureException e) { log.LogWarning(e, "Entry {EntryId} failed verification and was not shown.", stored.Id); }
        }
        return result;
    }

    public static bool HasLoinc(JsonElement resource, string loinc) =>
        resource.TryGetProperty("code", out var code) && code.TryGetProperty("coding", out var coding) && coding.ValueKind == JsonValueKind.Array
        && coding.EnumerateArray().Any(c => c.TryGetProperty("code", out var v) && v.GetString() == loinc);
}
