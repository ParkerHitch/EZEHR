using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    OrganizationManager organizationManager,
    TimeProvider time,
    IOptions<AppOptions> options,
    ILogger<RecordManager> logger
)
{
    public const string EgfrLoincCode = "98979-8";

    /// <summary>
    /// Records are signed when they are sent, so an incoming JWS iat must be recent: at most this far ahead (clock
    /// skew) and this old. The signer's chain is checked at iat, so an unbounded iat would let a key sign after
    /// its certificate expired by backdating.
    /// </summary>
    public static readonly TimeSpan SignedAtClockSkew = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MaxSignedAtAge = TimeSpan.FromHours(1);

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
            return new PushSignedRecordsResult(false, unverified.Reason, null, 0, 0, 0);
        }
        if (caller is not VerifiedCaller { Party.Kind: PartyKind.Provider } verified)
        {
            return new PushSignedRecordsResult(false, DenyReason.WrongPartyKind, null, 0, 0, 0);
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
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        List<(string SourceKey, VerifiedEntry Entry, string Jws)> checkedEntries = [];
        foreach (string jws in signedEntries)
        {
            (VerifiedEntry? entry, string? sourceKey, RecordError? error) =
                VerifyEntrySignedByCaller(jws, provider, directory);
            if (error is RecordError refused)
            {
                return PushRefused(refused);
            }
            checkedEntries.Add((sourceKey!, entry!, jws));
        }

        int stored = 0;
        int alreadyStored = 0;
        int replaced = 0;
        foreach ((string sourceKey, VerifiedEntry entry, string jws) in checkedEntries)
        {
            (StoreOutcome outcome, Guid _) = await StoreEntryAsync(
                patientId,
                sourceKey,
                entry.ResourceType,
                jws
            );
            if (outcome == StoreOutcome.Stored)
            {
                stored++;
            }
            else if (outcome == StoreOutcome.Replaced)
            {
                replaced++;
            }
            else
            {
                alreadyStored++;
            }
        }
        await auditManager.AppendAsync(patientId, provider, "PushSignedRecords", null, true, null);
        return new PushSignedRecordsResult(true, null, null, stored, alreadyStored, replaced);
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
        VerifiedEntries observations = await ReadVerifiedEntriesAsync(patientId, resourceType =>
            resourceType == "Observation"
        );
        List<SignedEntry> labs = observations
            .Entries.Where(entry => HasLoincCode(entry.Resource, EgfrLoincCode))
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
            return new ReadFullRecordResult(false, decision.Reason, [], decision.Grant, null);
        }
        await using SqlConnection connection = await db.OpenAsync();
        string? patientName = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT display_name FROM patients WHERE id = @patientId",
            new { patientId }
        );
        VerifiedEntries record = await ReadVerifiedEntriesAsync(patientId, null);
        return new ReadFullRecordResult(
            true,
            null,
            record.Entries,
            decision.Grant,
            patientName,
            record.UnverifiedCount
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
        // Read before the entries, so a record stored in between shows up on the next poll.
        string recordVersion = await ReadRecordVersionAsync(connection, patientId);
        VerifiedEntries record = await ReadVerifiedEntriesAsync(patientId, null);
        Timeline timeline = TimelineBuilder.Build(record.Entries) with
        {
            UnverifiedCount = record.UnverifiedCount,
        };
        return new PatientHome(name, timeline, recordVersion);
    }

    /// <summary>
    /// The home timeline again, or null if no entry was added, corrected or removed since
    /// <paramref name="knownVersion"/>. The check reads no ciphertext, so the home page can poll it every 2 s
    /// without a key unwrap each time.
    /// </summary>
    public async Task<PatientHome?> ReadPatientHomeIfChangedAsync(
        Guid patientId,
        string? knownVersion
    )
    {
        await using (SqlConnection connection = await db.OpenAsync())
        {
            if (await ReadRecordVersionAsync(connection, patientId) == knownVersion)
            {
                return null;
            }
        }
        return await ReadPatientHomeAsync(patientId);
    }

    /// <summary>
    /// A fingerprint of the patient's stored entries. A correction re-encrypts with a fresh IV, so it changes too.
    /// </summary>
    private static async Task<string> ReadRecordVersionAsync(
        SqlConnection connection,
        Guid patientId
    )
    {
        return await connection.QuerySingleAsync<string>(
            """
            SELECT CONCAT(COUNT(*), ':', CHECKSUM_AGG(BINARY_CHECKSUM(id, CAST(iv AS VARBINARY(64)))))
            FROM entries
            WHERE patient_id = @patientId
            """,
            new { patientId }
        );
    }

    /// <summary>
    /// The patient's own whole record as a FHIR R4 collection Bundle. Each resource is followed by a Provenance
    /// carrying the original JWS, so anyone can verify who signed it. Not logged, like ReadPatientHomeAsync.
    /// </summary>
    public async Task<ExportPatientRecordResult> ExportPatientRecordAsync(Guid patientId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        string name = await connection.QuerySingleAsync<string>(
            "SELECT display_name FROM patients WHERE id = @patientId",
            new { patientId }
        );
        JsonArray bundleEntries = [];
        foreach (SignedEntry entry in (await ReadVerifiedEntriesAsync(patientId, null)).Entries)
        {
            // Resource ids are only unique per signer, so references go by the entry's own fullUrl.
            string resourceUrl = $"urn:uuid:{entry.Id}";
            Guid provenanceId = Guid.NewGuid();
            string signer = $"{entry.Signer.DisplayName}, {entry.Signer.OrgName}";
            bundleEntries.Add(
                new JsonObject
                {
                    ["fullUrl"] = resourceUrl,
                    ["resource"] = JsonNode.Parse(entry.Resource.GetRawText()),
                }
            );
            bundleEntries.Add(
                new JsonObject
                {
                    ["fullUrl"] = $"urn:uuid:{provenanceId}",
                    ["resource"] = new JsonObject
                    {
                        ["resourceType"] = "Provenance",
                        ["id"] = provenanceId.ToString(),
                        ["target"] = new JsonArray(new JsonObject { ["reference"] = resourceUrl }),
                        ["recorded"] = entry.SignedAt.ToString("o"),
                        ["agent"] = new JsonArray(
                            new JsonObject { ["who"] = new JsonObject { ["display"] = signer } }
                        ),
                        ["signature"] = new JsonArray(
                            new JsonObject
                            {
                                ["type"] = new JsonArray(
                                    new JsonObject
                                    {
                                        ["system"] = "urn:iso-astm:E1762-95:2013",
                                        ["code"] = "1.2.840.10065.1.12.1.1",
                                        ["display"] = "Author's Signature",
                                    }
                                ),
                                ["when"] = entry.SignedAt.ToString("o"),
                                ["who"] = new JsonObject { ["display"] = signer },
                                ["sigFormat"] = "application/jose",
                                ["data"] = Convert.ToBase64String(
                                    Encoding.UTF8.GetBytes(entry.Jws)
                                ),
                            }
                        ),
                    },
                }
            );
        }
        JsonObject bundle = new()
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["type"] = "collection",
            ["timestamp"] = time.GetUtcNow().ToString("o"),
            ["entry"] = bundleEntries,
        };
        return new ExportPatientRecordResult(ExportFileName(name), bundle);
    }

    /// <summary>"Sam Taylor" → "sam-taylor-record.json".</summary>
    private static string ExportFileName(string patientName)
    {
        string slug = Regex.Replace(patientName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "health-record.json" : $"{slug}-record.json";
    }

    /// <summary>
    /// A note (a FHIR Communication) signed by the caller itself. Idempotent per note id; a changed note with the
    /// same id replaces the stored one.
    /// </summary>
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
            ((VerifiedCaller)caller).Party,
            await organizationManager.LoadDirectoryAsync()
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

        (StoreOutcome outcome, Guid entryId) = await StoreEntryAsync(
            patientId,
            sourceKey!,
            "Communication",
            jws
        );
        bool stored = outcome is StoreOutcome.Stored or StoreOutcome.Replaced;
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
    /// Stores a signed record once per (patient, sourceKey). Sending the same resource again, even re-signed,
    /// changes nothing. A changed resource for the same key is the signer correcting its record: it replaces the
    /// stored one unless it was signed earlier. Checks no access: callers must have.
    /// </summary>
    public async Task<(StoreOutcome Outcome, Guid EntryId)> StoreEntryAsync(
        Guid patientId,
        string sourceKey,
        string resourceType,
        string jws
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        byte[] dataKey = await UnwrapPatientKeyAsync(connection, patientId);
        try
        {
            EntryRow? existing = await FindEntryAsync(connection, patientId, sourceKey);
            if (existing is not null)
            {
                return await ReplaceIfCorrectedAsync(connection, dataKey, patientId, existing, jws);
            }

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
                return (StoreOutcome.Stored, entryId);
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
        return (
            StoreOutcome.AlreadyStored,
            (await FindEntryAsync(connection, patientId, sourceKey))!.Id
        );
    }

    private static async Task<(StoreOutcome Outcome, Guid EntryId)> ReplaceIfCorrectedAsync(
        SqlConnection connection,
        byte[] dataKey,
        Guid patientId,
        EntryRow existing,
        string jws
    )
    {
        string associatedData = AssociatedData(patientId, existing.Id);
        string storedJws = RecordCipher.Decrypt(
            dataKey,
            associatedData,
            existing.Iv,
            existing.Ciphertext
        );
        // Re-signing gives a new header (a new iat) over the same payload; only a changed payload is a correction.
        if (PayloadOf(storedJws) == PayloadOf(jws))
        {
            return (StoreOutcome.AlreadyStored, existing.Id);
        }
        // A replayed older version must not undo a correction.
        if (
            RecordVerifier.ReadIssuedAt(storedJws) is DateTimeOffset storedSignedAt
            && RecordVerifier.ReadIssuedAt(jws) is DateTimeOffset signedAt
            && signedAt < storedSignedAt
        )
        {
            return (StoreOutcome.OlderThanStored, existing.Id);
        }

        (byte[] iv, byte[] ciphertext) = RecordCipher.Encrypt(dataKey, associatedData, jws);
        // Conditional on the version that was compared, so of two corrections racing only one applies.
        int rows = await connection.ExecuteAsync(
            """
            UPDATE entries SET iv = @iv, ciphertext = @ciphertext
            WHERE id = @entryId AND ciphertext = @storedCiphertext
            """,
            new
            {
                iv,
                ciphertext,
                entryId = existing.Id,
                storedCiphertext = existing.Ciphertext,
            }
        );
        return (rows == 1 ? StoreOutcome.Replaced : StoreOutcome.AlreadyStored, existing.Id);
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
    /// Verifies a JWS from a caller: a good signature, signed by the caller itself, recently, and a resource id.
    /// Returns the entry and its source key ({signerUri}|{resourceType}/{id}), or why it was refused.
    /// </summary>
    private (VerifiedEntry? Entry, string? SourceKey, RecordError? Error) VerifyEntrySignedByCaller(
        string jws,
        Party caller,
        OrganizationDirectory directory
    )
    {
        VerifiedEntry entry;
        try
        {
            entry = RecordVerifier.Verify(Guid.Empty, jws, _trustAnchors, directory);
        }
        catch (RecordSignatureException)
        {
            return (null, null, RecordError.BadSignature);
        }
        if (entry.Signer.SubjectUri != caller.SubjectUri)
        {
            return (null, null, RecordError.SignerMismatch);
        }
        DateTimeOffset now = time.GetUtcNow();
        if (entry.SignedAt > now + SignedAtClockSkew || entry.SignedAt < now - MaxSignedAtAge)
        {
            return (null, null, RecordError.SignedAtOutOfRange);
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

    /// <summary>Entries that fail verification are left out and counted, so pages can say how many.</summary>
    private async Task<VerifiedEntries> ReadVerifiedEntriesAsync(
        Guid patientId,
        Func<string, bool>? resourceTypeFilter
    )
    {
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        List<SignedEntry> entries = [];
        int unverifiedCount = 0;
        foreach (
            DecryptedEntry decrypted in await DecryptEntriesAsync(patientId, resourceTypeFilter)
        )
        {
            try
            {
                VerifiedEntry verified = RecordVerifier.Verify(
                    decrypted.Id,
                    decrypted.Jws,
                    _trustAnchors,
                    directory
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
                unverifiedCount++;
                logger.LogWarning(
                    exception,
                    "Entry {EntryId} failed verification and was not shown.",
                    decrypted.Id
                );
            }
        }
        return new VerifiedEntries(entries, unverifiedCount);
    }

    private static PushSignedRecordsResult PushRefused(RecordError error)
    {
        return new PushSignedRecordsResult(true, null, error, 0, 0, 0);
    }

    private static async Task<EntryRow?> FindEntryAsync(
        SqlConnection connection,
        Guid patientId,
        string sourceKey
    )
    {
        return await connection.QuerySingleOrDefaultAsync<EntryRow>(
            """
            SELECT id AS Id, resource_type AS ResourceType, iv AS Iv, ciphertext AS Ciphertext
            FROM entries
            WHERE patient_id = @patientId AND source_key = @sourceKey
            """,
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

    /// <summary>The signed resource part of a compact JWS (header.payload.signature).</summary>
    private static string PayloadOf(string jws)
    {
        string[] parts = jws.Split('.');
        return parts.Length == 3 ? parts[1] : jws;
    }

    private static string AssociatedData(Guid patientId, Guid entryId)
    {
        return $"{patientId}:{entryId}";
    }

    private sealed record EntryRow(Guid Id, string ResourceType, byte[] Iv, byte[] Ciphertext);
}
