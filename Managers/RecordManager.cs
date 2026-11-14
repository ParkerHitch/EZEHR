using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>
/// Patients' signed records, encrypted under each patient's own key (envelope encryption). The patient key is
/// unwrapped on every call, so every read is a Key Vault operation. Reads and notes from other parties go through
/// AccessManager; PutAsync and ReadAsync are the raw store underneath and check nothing.
/// </summary>
public sealed class RecordManager(
    Db db,
    IKeyWrapService keyWrapService,
    AccessManager accessManager,
    AuditManager auditManager,
    InviteManager inviteManager,
    TimeProvider time,
    IOptions<AppOptions> options,
    ILogger<RecordManager> logger
)
{
    public const string EgfrLoinc = "98979-8";

    private readonly TrustAnchors _trustAnchors = CertificateValidator.LoadTrustAnchors(
        options.Value
    );

    /// <summary>A provider pushes signed records for one of its patients. Everything is verified before anything is stored.</summary>
    public async Task<IngestResult> IngestAsync(
        Caller caller,
        string providerPatientId,
        IReadOnlyList<string> entries
    )
    {
        if (caller is UnverifiedCaller unverified)
        {
            return new IngestResult(false, unverified.Reason, null, 0, 0);
        }
        if (caller is not VerifiedCaller { Party.Kind: PartyKind.Provider } verified)
        {
            return new IngestResult(false, DenyReason.WrongPartyKind, null, 0, 0);
        }
        Party provider = verified.Party;

        Guid? linkedPatient = await inviteManager.LinkedPatientAsync(
            provider.SubjectUri,
            providerPatientId
        );
        if (linkedPatient is not Guid patientId)
        {
            return IngestRefused(RecordError.PatientNotLinked);
        }

        // Verify everything before storing anything, so a bad batch stores nothing.
        List<(string SourceKey, VerifiedEntry Entry, string Jws)> checkedEntries = [];
        foreach (string jws in entries)
        {
            (VerifiedEntry? entry, string? sourceKey, RecordError? error) = CheckSigned(
                jws,
                provider
            );
            if (error is RecordError refused)
            {
                return IngestRefused(refused);
            }
            checkedEntries.Add((sourceKey!, entry!, jws));
        }

        int inserted = 0;
        int skipped = 0;
        foreach ((string sourceKey, VerifiedEntry entry, string jws) in checkedEntries)
        {
            (bool added, Guid _) = await PutAsync(patientId, sourceKey, entry.ResourceType, jws);
            if (added)
            {
                inserted++;
            }
            else
            {
                skipped++;
            }
        }
        await auditManager.AppendAsync(patientId, provider, "Ingest", null, true, null);
        return new IngestResult(true, null, null, inserted, skipped);
    }

    /// <summary>The patient's eGFR lab results, for an app with a grant. Logged by the access check.</summary>
    public async Task<LabsResult> ReadLabsAsync(Guid patientId, Caller caller)
    {
        AccessDecision decision = await accessManager.AuthorizeAsync(
            patientId,
            AccessAction.ReadLabs,
            caller,
            null
        );
        if (!decision.Allowed)
        {
            return new LabsResult(false, decision.Reason, []);
        }
        IReadOnlyList<SignedEntry> observations = await ReadVerifiedAsync(patientId, resourceType =>
            resourceType == "Observation"
        );
        List<SignedEntry> labs = observations
            .Where(entry => HasLoinc(entry.Resource, EgfrLoinc))
            .ToList();
        return new LabsResult(true, null, labs);
    }

    /// <summary>The whole record. Logged by the access check, with the purpose the caller claimed.</summary>
    public async Task<RecordResult> ReadRecordAsync(
        Guid patientId,
        Caller caller,
        string? claimedPurpose
    )
    {
        AccessDecision decision = await accessManager.AuthorizeAsync(
            patientId,
            AccessAction.ReadRecord,
            caller,
            claimedPurpose
        );
        if (!decision.Allowed)
        {
            return new RecordResult(false, decision.Reason, []);
        }
        return new RecordResult(true, null, await ReadVerifiedAsync(patientId, null));
    }

    /// <summary>A note (a FHIR Communication) signed by the caller itself. Idempotent per note id.</summary>
    public async Task<NoteResult> WriteNoteAsync(Guid patientId, Caller caller, string jws)
    {
        AccessDecision decision = await accessManager.AuthorizeAsync(
            patientId,
            AccessAction.WriteNote,
            caller,
            null
        );
        if (!decision.Allowed)
        {
            return new NoteResult(false, decision.Reason, null, false, null);
        }

        (VerifiedEntry? note, string? sourceKey, RecordError? error) = CheckSigned(
            jws,
            ((VerifiedCaller)caller).Party
        );
        if (error is RecordError refused)
        {
            return new NoteResult(true, null, refused, false, null);
        }
        if (note!.ResourceType != "Communication")
        {
            return new NoteResult(true, null, RecordError.NotACommunication, false, null);
        }

        (bool inserted, Guid entryId) = await PutAsync(patientId, sourceKey!, "Communication", jws);
        return new NoteResult(true, null, null, inserted, entryId);
    }

    /// <summary>Generates a new patient data key and returns it wrapped. The plaintext key is erased.</summary>
    public async Task<byte[]> CreatePatientKeyAsync()
    {
        byte[] dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            return await keyWrapService.WrapAsync(dek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Creates a patient row with a fresh wrapped data key.</summary>
    public async Task<Guid> CreatePatientAsync(string displayName)
    {
        Guid patientId = Guid.NewGuid();
        byte[] wrappedDek = await CreatePatientKeyAsync();
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO patients(id, display_name, wrapped_dek, created_at)
            VALUES (@patientId, @displayName, @wrappedDek, @createdAt)
            """,
            new
            {
                patientId,
                displayName,
                wrappedDek,
                createdAt = time.GetUtcNow(),
            }
        );
        return patientId;
    }

    public async Task<bool> PatientExistsAsync(Guid patientId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        int count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM patients WHERE id = @patientId",
            new { patientId }
        );
        return count == 1;
    }

    /// <summary>
    /// Stores a signed record once per (patient, sourceKey). A repeat returns the existing id with
    /// Inserted = false. Checks no access: callers must have.
    /// </summary>
    public async Task<(bool Inserted, Guid Id)> PutAsync(
        Guid patientId,
        string sourceKey,
        string resourceType,
        string jws
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        Guid? existing = await ExistingAsync(connection, patientId, sourceKey);
        if (existing is Guid found)
        {
            return (false, found);
        }

        byte[] dek = await UnwrapAsync(connection, patientId);
        try
        {
            Guid entryId = Guid.NewGuid();
            (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(
                dek,
                Aad(patientId, entryId),
                jws
            );
            int rows = await connection.ExecuteAsync(
                """
                INSERT INTO entries(id, patient_id, source_key, resource_type, iv, ciphertext, created_at)
                SELECT @entryId, @patientId, @sourceKey, @resourceType, @iv, @ciphertext, @createdAt
                WHERE NOT EXISTS (
                    SELECT 1 FROM entries WHERE patient_id = @patientId AND source_key = @sourceKey)
                """,
                new
                {
                    entryId,
                    patientId,
                    sourceKey,
                    resourceType,
                    iv,
                    ciphertext,
                    createdAt = time.GetUtcNow(),
                }
            );
            if (rows == 1)
            {
                return (true, entryId);
            }
        }
        catch (SqlException exception) when (Db.IsUniqueViolation(exception))
        {
            // Another request stored the same entry between the check and the insert.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
        return (false, (await ExistingAsync(connection, patientId, sourceKey))!.Value);
    }

    /// <summary>Decrypts a patient's entries, oldest first. Checks no access: callers must have.</summary>
    public async Task<IReadOnlyList<StoredEntry>> ReadAsync(
        Guid patientId,
        Func<string, bool>? resourceTypeFilter
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        byte[] dek = await UnwrapAsync(connection, patientId);
        try
        {
            IEnumerable<EntryRow> rows = await connection.QueryAsync<EntryRow>(
                """
                SELECT id AS Id, resource_type AS ResourceType, iv AS Iv, ciphertext AS Ciphertext
                FROM entries
                WHERE patient_id = @patientId
                ORDER BY created_at, id
                """,
                new { patientId }
            );
            List<StoredEntry> entries = [];
            foreach (EntryRow row in rows)
            {
                if (resourceTypeFilter is not null && !resourceTypeFilter(row.ResourceType))
                {
                    continue;
                }
                string jws = RecordCipher.Decrypt(
                    dek,
                    Aad(patientId, row.Id),
                    row.Iv,
                    row.Ciphertext
                );
                entries.Add(new StoredEntry(row.Id, row.ResourceType, jws));
            }
            return entries;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public static bool HasLoinc(JsonElement resource, string loinc)
    {
        return resource.TryGetProperty("code", out JsonElement code)
            && code.TryGetProperty("coding", out JsonElement coding)
            && coding.ValueKind == JsonValueKind.Array
            && coding
                .EnumerateArray()
                .Any(c =>
                    c.TryGetProperty("code", out JsonElement value) && value.GetString() == loinc
                );
    }

    /// <summary>
    /// Verifies a JWS from a caller: a good signature, signed by the caller itself, and a resource id.
    /// Returns the entry and its source key ({signerUri}|{resourceType}/{id}), or why it was refused.
    /// </summary>
    private (VerifiedEntry? Entry, string? SourceKey, RecordError? Error) CheckSigned(
        string jws,
        Party caller
    )
    {
        VerifiedEntry entry;
        try
        {
            entry = RecordVerifier.Verify(Guid.Empty, jws, _trustAnchors);
        }
        catch (RecordSignatureException)
        {
            return (null, null, RecordError.BadSignature);
        }
        if (entry.Signer.SubjectUri != caller.SubjectUri)
        {
            return (null, null, RecordError.SignerMismatch);
        }
        // FHIR ids are at most 64 characters, which also keeps the source key inside its column.
        if (
            !entry.Resource.TryGetProperty("id", out JsonElement id)
            || id.ValueKind != JsonValueKind.String
            || id.GetString() is not { Length: > 0 and <= 64 } resourceId
        )
        {
            return (null, null, RecordError.ResourceIdRequired);
        }
        return (entry, $"{entry.Signer.SubjectUri}|{entry.ResourceType}/{resourceId}", null);
    }

    private async Task<IReadOnlyList<SignedEntry>> ReadVerifiedAsync(
        Guid patientId,
        Func<string, bool>? resourceTypeFilter
    )
    {
        List<SignedEntry> entries = [];
        foreach (StoredEntry stored in await ReadAsync(patientId, resourceTypeFilter))
        {
            try
            {
                VerifiedEntry verified = RecordVerifier.Verify(
                    stored.Id,
                    stored.Jws,
                    _trustAnchors
                );
                entries.Add(
                    new SignedEntry(
                        verified.Id,
                        verified.ResourceType,
                        verified.Resource,
                        verified.Signer,
                        verified.SignedAt,
                        verified.WrittenBy,
                        stored.Jws
                    )
                );
            }
            catch (RecordSignatureException exception)
            {
                logger.LogWarning(
                    exception,
                    "Entry {EntryId} failed verification and was not shown.",
                    stored.Id
                );
            }
        }
        return entries;
    }

    private static IngestResult IngestRefused(RecordError error)
    {
        return new IngestResult(true, null, error, 0, 0);
    }

    private static async Task<Guid?> ExistingAsync(
        SqlConnection connection,
        Guid patientId,
        string sourceKey
    )
    {
        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM entries WHERE patient_id = @patientId AND source_key = @sourceKey",
            new { patientId, sourceKey }
        );
    }

    private async Task<byte[]> UnwrapAsync(SqlConnection connection, Guid patientId)
    {
        byte[] wrappedDek = await connection.QuerySingleOrDefaultAsync<byte[]>(
            "SELECT wrapped_dek FROM patients WHERE id = @patientId",
            new { patientId }
        ) ?? throw new InvalidOperationException("Unknown patient.");
        return await keyWrapService.UnwrapAsync(wrappedDek);
    }

    private static string Aad(Guid patientId, Guid entryId)
    {
        return $"{patientId}:{entryId}";
    }

    private sealed record EntryRow(Guid Id, string ResourceType, byte[] Iv, byte[] Ciphertext);
}
