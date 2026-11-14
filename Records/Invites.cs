using Dapper;
using EZEHR.Access;
using EZEHR.Data;
using Microsoft.Data.Sqlite;

namespace EZEHR.Records;

public sealed class InviteException(string code) : Exception(code)
{
    /// <summary>invite_unknown, invite_used, invite_expired or invite_linked_elsewhere.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Provider-issued enrollment invites, like a patient-portal activation code. Redeeming one links the provider's
/// patient id to the EZEHR patient, which is what lets that provider push records.
/// </summary>
public sealed class InviteService(SqliteConnection db, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public string Create(string providerUri, string providerPatientId)
    {
        var code = Codes.New();
        db.Execute("INSERT INTO invites(code, provider_uri, provider_patient_id, expires_at) VALUES (@code, @providerUri, @providerPatientId, @expires)",
            new { code, providerUri, providerPatientId, expires = Db.Time(time.GetUtcNow() + Lifetime) });
        return code;
    }

    /// <summary>Checks an invite without using it (enrollment shows who invited the patient before the passkey is made).</summary>
    public (string ProviderUri, string ProviderPatientId) Peek(string code)
    {
        var row = Find(Codes.Normalize(code));
        return (row.ProviderUri, row.ProviderPatientId);
    }

    /// <summary>Links the invite's provider record to the patient and marks the invite used. Single use.</summary>
    public (string ProviderUri, string ProviderPatientId) Redeem(string code, Guid patientId)
    {
        code = Codes.Normalize(code);
        using var tx = db.BeginTransaction();
        var row = Find(code, tx);
        var marked = db.Execute("UPDATE invites SET redeemed_at = @at WHERE code = @code AND redeemed_at IS NULL",
            new { code, at = Db.Time(time.GetUtcNow()) }, tx);
        if (marked != 1) throw new InviteException("invite_used");

        var linkedTo = db.QuerySingleOrDefault<string>(
            "SELECT patient_id FROM provider_links WHERE provider_uri = @ProviderUri AND provider_patient_id = @ProviderPatientId", row, tx);
        if (linkedTo is not null && linkedTo != patientId.ToString()) throw new InviteException("invite_linked_elsewhere");
        if (linkedTo is null)
            db.Execute("INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id) VALUES (@patient, @ProviderUri, @ProviderPatientId)",
                new { patient = patientId.ToString(), row.ProviderUri, row.ProviderPatientId }, tx);
        tx.Commit();
        return (row.ProviderUri, row.ProviderPatientId);
    }

    /// <summary>The EZEHR patient linked to a provider's own patient id, if any.</summary>
    public Guid? LinkedPatient(string providerUri, string providerPatientId)
    {
        var id = db.QuerySingleOrDefault<string>(
            "SELECT patient_id FROM provider_links WHERE provider_uri = @providerUri AND provider_patient_id = @providerPatientId",
            new { providerUri, providerPatientId });
        return id is null ? null : Guid.Parse(id);
    }

    private InviteRow Find(string code, SqliteTransaction? tx = null)
    {
        var row = db.QuerySingleOrDefault<InviteRow>(
            "SELECT provider_uri AS ProviderUri, provider_patient_id AS ProviderPatientId, expires_at AS ExpiresAt, redeemed_at AS RedeemedAt FROM invites WHERE code = @code",
            new { code }, tx) ?? throw new InviteException("invite_unknown");
        if (row.RedeemedAt is not null) throw new InviteException("invite_used");
        if (time.GetUtcNow() > Db.Time(row.ExpiresAt)) throw new InviteException("invite_expired");
        return row;
    }

    private sealed class InviteRow
    {
        public string ProviderUri { get; set; } = "";
        public string ProviderPatientId { get; set; } = "";
        public string ExpiresAt { get; set; } = "";
        public string? RedeemedAt { get; set; }
    }
}
