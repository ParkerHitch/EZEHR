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
/// AccessManager; StoreEntryAsync and DecryptEntriesAsync are the raw store underneath and check nothing.
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
    public const string EgfrLoincCode = "98979-8";

    private readonly TrustAnchors _trustAnchors = CertificateValidator.LoadTrustAnchors(
        options.Value
    );

    /// <summary>A provider pushes signed records for one of its patients. Everything is verified before anything is stored.</summary>
    public async Task<PushSignedRecordsResult> PushSignedRecordsAsync(
        Caller caller,
        string providerPatientId,
        IReadOnlyList<string> signedEntries
    )
    {
        if (caller is UnverifiedCaller unverified)
        {
            return new PushSignedRecordsResult(false, unverified.Reason, null, 0, 0);
        }
        if (caller is not VerifiedCaller { Party.Kind: PartyKind.Provider } verified)
        {
            return new PushSignedRecordsResult(false, DenyReason.WrongPartyKind, null, 0, 0);
        }
        Party provider = verified.Party;

        Guid? linkedPatient = await inviteManager.FindLinkedPatientAsync(
            provider.SubjectUri,
            providerPatientId
        );
        if (linkedPatient is not Guid patientId)
        {
            return PushRefused(RecordError.PatientNotLinked);
        }

        // Verify everything before storing anything, so a bad batch stores nothing.
        List<(string SourceKey, VerifiedEntry Entry, string Jws)> checkedEntries = [];
        foreach (string jws in signedEntries)
        {
            (VerifiedEntry? entry, string? sourceKey, RecordError? error) =
                VerifyEntrySignedByCaller(jws, provider);
            if (error is RecordError refused)
            {
                return PushRefused(refused);
            }
            checkedEntries.Add((sourceKey!, entry!, jws));
        }

        int stored = 0;
        int alreadyStored = 0;
        foreach ((string sourceKey, VerifiedEntry entry, string jws) in checkedEntries)
        {
            (bool newlyStored, Guid _) = await StoreEntryAsync(
                patientId,
                sourceKey,
                entry.ResourceType,
                jws
            );
            if (newlyStored)
            {
                stored++;
            }
            else
            {
                alreadyStored++;
            }
        }
        await auditManager.AppendAsync(patientId, provider, "PushSignedRecords", null, true, null);
        return new PushSignedRecordsResult(true, null, null, stored, alreadyStored);
    }

    /// <summary>The patient's eGFR lab results, for an app with a grant. Logged by the access check.</summary>
    public async Task<ReadEgfrLabsResult> ReadEgfrLabsAsync(Guid patientId, Caller caller)
    {
        AccessDecision decision = await accessManager.AuthorizeAsync(
            patientId,
            AccessAction.ReadEgfrLabs,
            caller,
            null
        );
        if (!decision.Allowed)
        {
            return new ReadEgfrLabsResult(false, decision.Reason, []);
        }
        IReadOnlyList<SignedEntry> observations = await ReadVerifiedEntriesAsync(
            patientId,
            resourceType => resourceType == "Observation"
        );
        List<SignedEntry> labs = observations
            .Where(entry => HasLoincCode(entry.Resource, EgfrLoincCode))
            .ToList();
        return new ReadEgfrLabsResult(true, null, labs);
    }

    /// <summary>The whole record. Logged by the access check, with the purpose the caller claimed.</summary>
    public async Task<ReadFullRecordResult> ReadFullRecordAsync(
        Guid patientId,
        Caller caller,
        string? claimedPurpose
    )
    {
        AccessDecision decision = await accessManager.AuthorizeAsync(
            patientId,
            AccessAction.ReadFullRecord,
            caller,
            claimedPurpose
        );
        if (!decision.Allowed)
        {
            return new ReadFullRecordResult(false, decision.Reason, []);
        }
        return new ReadFullRecordResult(
            true,
            null,
            await ReadVerifiedEntriesAsync(patientId, null)
        );
    }

    /// <summary>
    /// The patient's own record, as a timeline. Every entry is verified like any other read; the patient's own
    /// reads go through the patient cookie, not the access check, and aren't logged.
    /// </summary>
    public async Task<PatientHome> ReadPatientHomeAsync(Guid patientId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        string name = await connection.QuerySingleAsync<string>(
            "SELECT display_name FROM patients WHERE id = @patientId",
            new { patientId }
        );
        Timeline timeline = TimelineBuilder.Build(await ReadVerifiedEntriesAsync(patientId, null));
        return new PatientHome(name, timeline);
    }

    /// <summary>A note (a FHIR Communication) signed by the caller itself. Idempotent per note id.</summary>
    public async Task<WriteSignedNoteResult> WriteSignedNoteAsync(
        Guid patientId,
        Caller caller,
        string jws
    )
    {
        AccessDecision decision = await accessManager.AuthorizeAsync(
            patientId,
            AccessAction.WriteSignedNote,
            caller,
            null
        );
        if (!decision.Allowed)
        {
            return new WriteSignedNoteResult(false, decision.Reason, null, false, null);
        }

        (VerifiedEntry? note, string? sourceKey, RecordError? error) = VerifyEntrySignedByCaller(
            jws,
            ((VerifiedCaller)caller).Party
        );
        if (error is RecordError refused)
        {
            return new WriteSignedNoteResult(true, null, refused, false, null);
        }
        if (note!.ResourceType != "Communication")
        {
            return new WriteSignedNoteResult(
                true,
                null,
                RecordError.NotACommunication,
                false,
                null
            );
        }

        (bool stored, Guid entryId) = await StoreEntryAsync(
            patientId,
            sourceKey!,
            "Communication",
            jws
        );
        return new WriteSignedNoteResult(true, null, null, stored, entryId);
    }

    /// <summary>Generates a new patient data key and returns it wrappedKey. The plaintext key is erased.</summary>
    public async Task<byte[]> CreatePatientKeyAsync()
    {
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            return await keyWrapService.WrapAsync(dataKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Creates a patient row with a fresh wrapped data key.</summary>
    public async Task<Guid> CreatePatientAsync(string displayName)
    {
        Guid patientId = Guid.NewGuid();
        byte[] wrappedDataKey = await CreatePatientKeyAsync();
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO patients(id, display_name, wrapped_dek, created_at)
            VALUES (@patientId, @displayName, @wrappedDataKey, @createdAt)
            """,
            new
            {
                patientId,
                displayName,
                wrappedDataKey,
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
    /// Stored = false. Checks no access: callers must have.
    /// </summary>
    public async Task<(bool Stored, Guid EntryId)> StoreEntryAsync(
        Guid patientId,
        string sourceKey,
        string resourceType,
        string jws
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        Guid? existing = await FindEntryIdAsync(connection, patientId, sourceKey);
        if (existing is Guid found)
        {
            return (false, found);
        }

        byte[] dataKey = await UnwrapPatientKeyAsync(connection, patientId);
        try
        {
            Guid entryId = Guid.NewGuid();
            (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(
                dataKey,
                AssociatedData(patientId, entryId),
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
            CryptographicOperations.ZeroMemory(dataKey);
        }
        return (false, (await FindEntryIdAsync(connection, patientId, sourceKey))!.Value);
    }

    /// <summary>Decrypts a patient's entries, oldest first. Checks no access: callers must have.</summary>
    public async Task<IReadOnlyList<DecryptedEntry>> DecryptEntriesAsync(
        Guid patientId,
        Func<string, bool>? resourceTypeFilter
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        byte[] dataKey = await UnwrapPatientKeyAsync(connection, patientId);
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
            List<DecryptedEntry> entries = [];
            foreach (EntryRow row in rows)
            {
                if (resourceTypeFilter is not null && !resourceTypeFilter(row.ResourceType))
                {
                    continue;
                }
                string jws = RecordCipher.Decrypt(
                    dataKey,
                    AssociatedData(patientId, row.Id),
                    row.Iv,
                    row.Ciphertext
                );
                entries.Add(new DecryptedEntry(row.Id, row.ResourceType, jws));
            }
            return entries;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public static bool HasLoincCode(JsonElement resource, string loinc)
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
    private (VerifiedEntry? Entry, string? SourceKey, RecordError? Error) VerifyEntrySignedByCaller(
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
            return (null, null, RecordError.MissingOrInvalidResourceId);
        }
        return (entry, $"{entry.Signer.SubjectUri}|{entry.ResourceType}/{resourceId}", null);
    }

    private async Task<IReadOnlyList<SignedEntry>> ReadVerifiedEntriesAsync(
        Guid patientId,
        Func<string, bool>? resourceTypeFilter
    )
    {
        List<SignedEntry> entries = [];
        foreach (
            DecryptedEntry decrypted in await DecryptEntriesAsync(patientId, resourceTypeFilter)
        )
        {
            try
            {
                VerifiedEntry verified = RecordVerifier.Verify(
                    decrypted.Id,
                    decrypted.Jws,
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
                        decrypted.Jws
                    )
                );
            }
            catch (RecordSignatureException exception)
            {
                logger.LogWarning(
                    exception,
                    "Entry {EntryId} failed verification and was not shown.",
                    decrypted.Id
                );
            }
        }
        return entries;
    }

    private static PushSignedRecordsResult PushRefused(RecordError error)
    {
        return new PushSignedRecordsResult(true, null, error, 0, 0);
    }

    private static async Task<Guid?> FindEntryIdAsync(
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

    private async Task<byte[]> UnwrapPatientKeyAsync(SqlConnection connection, Guid patientId)
    {
        byte[] wrappedDataKey = await connection.QuerySingleOrDefaultAsync<byte[]>(
            "SELECT wrapped_dek FROM patients WHERE id = @patientId",
            new { patientId }
        ) ?? throw new InvalidOperationException("Unknown patient.");
        return await keyWrapService.UnwrapAsync(wrappedDataKey);
    }

    private static string AssociatedData(Guid patientId, Guid entryId)
    {
        return $"{patientId}:{entryId}";
    }

    private sealed record EntryRow(Guid Id, string ResourceType, byte[] Iv, byte[] Ciphertext);
}
