using System.Data.Common;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>
/// Provider-issued enrollment invites, like a patient-portal activation code. Redeeming one links the provider's
/// patient id to the EZEHR patient, which is what lets that provider push records. A link lets the provider write,
/// never read; the patient can see their links on the Access page and disconnect one.
/// </summary>
public sealed class InviteManager(
    Db db,
    OrganizationManager organizationManager,
    TimeProvider time,
    IOptions<AppOptions> options
)
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(
        options.Value.TimeZone
    );

    /// <summary>A provider asks for an invite for one of its patients. Only verified providers may.</summary>
    public async Task<CreateEnrollmentInviteResult> CreateEnrollmentInviteAsync(
        Caller caller,
        string providerPatientId
    )
    {
        if (caller is UnverifiedCaller unverified)
        {
            return new CreateEnrollmentInviteResult(false, unverified.Reason, null, null);
        }
        if (caller is not VerifiedCaller { Party.Kind: PartyKind.Provider } provider)
        {
            return new CreateEnrollmentInviteResult(false, DenyReason.WrongPartyKind, null, null);
        }

        string code = GrantManager.GenerateCode();
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO invites(code, provider_uri, provider_patient_id, expires_at)
            VALUES (@code, @providerUri, @providerPatientId, @expiresAt)
            """,
            new
            {
                code,
                providerUri = provider.Party.SubjectUri,
                providerPatientId,
                expiresAt = time.GetUtcNow() + InviteLifetime,
            }
        );
        string enrollmentUrl = $"{options.Value.Origin.TrimEnd('/')}/Patient/Enroll?invite={code}";
        return new CreateEnrollmentInviteResult(true, null, code, enrollmentUrl);
    }

    /// <summary>Checks an invite without using it (enrollment shows who invited the patient before the passkey is made).</summary>
    public async Task<InviteResult> CheckInviteAsync(string code)
    {
        await using SqlConnection connection = await db.OpenAsync();
        InviteRow? invite = await FindAsync(connection, null, GrantManager.NormalizeCode(code));
        if (WhyUnusable(invite) is InviteError error)
        {
            return Failed(error);
        }
        return Succeeded(invite!.ToLink());
    }

    /// <summary>Links the invite's provider record to the patient and marks the invite used. Single use.</summary>
    public async Task<InviteResult> RedeemInviteAsync(string code, Guid patientId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        InviteResult result = await RedeemInviteAsync(connection, transaction, code, patientId);
        if (result.Succeeded)
        {
            await transaction.CommitAsync();
        }
        return result;
    }

    /// <summary>
    /// The same, inside the caller's transaction, so enrollment can claim the invite and create the patient
    /// together. The caller commits.
    /// </summary>
    public async Task<InviteResult> RedeemInviteAsync(
        SqlConnection connection,
        DbTransaction transaction,
        string code,
        Guid patientId
    )
    {
        string normalizedCode = GrantManager.NormalizeCode(code);
        DateTimeOffset now = time.GetUtcNow();

        // The conditional UPDATE is the claim, so a double submit redeems once.
        ProviderLink? link = await connection.QuerySingleOrDefaultAsync<ProviderLink>(
            """
            UPDATE invites SET redeemed_at = @now
            OUTPUT inserted.provider_uri AS ProviderUri,
                   inserted.provider_patient_id AS ProviderPatientId
            WHERE code = @normalizedCode AND redeemed_at IS NULL AND expires_at >= @now
            """,
            new { normalizedCode, now },
            transaction
        );
        if (link is null)
        {
            InviteRow? invite = await FindAsync(connection, transaction, normalizedCode);
            return Failed(WhyUnusable(invite) ?? InviteError.InviteUsed);
        }

        Guid? linkedTo = await FindLinkedPatientAsync(connection, transaction, link);
        if (linkedTo is not null && linkedTo != patientId)
        {
            return Failed(InviteError.InviteLinkedElsewhere);
        }
        if (linkedTo is null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO provider_links(patient_id, provider_uri, provider_patient_id, linked_at)
                VALUES (@patientId, @ProviderUri, @ProviderPatientId, @now)
                """,
                new
                {
                    patientId,
                    link.ProviderUri,
                    link.ProviderPatientId,
                    now,
                },
                transaction
            );
        }
        return Succeeded(link);
    }

    /// <summary>The providers that can push records for the patient, oldest link first, as the Access page lists them.</summary>
    public async Task<IReadOnlyList<ConnectedProviderLine>> ListConnectedProvidersAsync(
        Guid patientId
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        IEnumerable<LinkRow> rows = await connection.QueryAsync<LinkRow>(
            """
            SELECT provider_uri AS ProviderUri, MIN(linked_at) AS LinkedAt
            FROM provider_links
            WHERE patient_id = @patientId
            GROUP BY provider_uri
            ORDER BY MIN(linked_at), provider_uri
            """,
            new { patientId }
        );
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        return rows.Select(row => new ConnectedProviderLine(
                row.ProviderUri,
                Copy.ConnectedProviderLine(
                    directory.FindByUri(row.ProviderUri)?.DisplayName ?? row.ProviderUri,
                    row.LinkedAt is DateTimeOffset linkedAt
                        ? LocalTime.Day(linkedAt, _timeZone)
                        : null
                )
            ))
            .ToList();
    }

    /// <summary>
    /// Removes the patient's links to a provider, so it can no longer push records. Records it already sent stay.
    /// Linking again takes a new invite. Another patient's links are untouched.
    /// </summary>
    public async Task DisconnectProviderAsync(Guid patientId, string providerUri)
    {
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            "DELETE FROM provider_links WHERE patient_id = @patientId AND provider_uri = @providerUri",
            new { patientId, providerUri }
        );
    }

    /// <summary>The EZEHR patient linked to a provider's own patient id, if any.</summary>
    public async Task<Guid?> FindLinkedPatientAsync(string providerUri, string providerPatientId)
    {
        await using SqlConnection connection = await db.OpenAsync();
        return await FindLinkedPatientAsync(
            connection,
            null,
            new ProviderLink(providerUri, providerPatientId)
        );
    }

    private static async Task<Guid?> FindLinkedPatientAsync(
        SqlConnection connection,
        DbTransaction? transaction,
        ProviderLink link
    )
    {
        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT patient_id FROM provider_links
            WHERE provider_uri = @ProviderUri AND provider_patient_id = @ProviderPatientId
            """,
            link,
            transaction
        );
    }

    private static async Task<InviteRow?> FindAsync(
        SqlConnection connection,
        DbTransaction? transaction,
        string normalizedCode
    )
    {
        return await connection.QuerySingleOrDefaultAsync<InviteRow>(
            """
            SELECT provider_uri AS ProviderUri, provider_patient_id AS ProviderPatientId,
                   expires_at AS ExpiresAt, redeemed_at AS RedeemedAt
            FROM invites
            WHERE code = @normalizedCode
            """,
            new { normalizedCode },
            transaction
        );
    }

    /// <summary>Why an invite can't be used, or null when it can.</summary>
    private InviteError? WhyUnusable(InviteRow? invite)
    {
        if (invite is null)
        {
            return InviteError.InviteUnknown;
        }
        if (invite.RedeemedAt is not null)
        {
            return InviteError.InviteUsed;
        }
        if (time.GetUtcNow() > invite.ExpiresAt)
        {
            return InviteError.InviteExpired;
        }
        return null;
    }

    private static InviteResult Succeeded(ProviderLink link)
    {
        return new InviteResult(true, link, null);
    }

    private static InviteResult Failed(InviteError error)
    {
        return new InviteResult(false, null, error);
    }

    private sealed record LinkRow(string ProviderUri, DateTimeOffset? LinkedAt);

    private sealed record InviteRow(
        string ProviderUri,
        string ProviderPatientId,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RedeemedAt
    )
    {
        public ProviderLink ToLink()
        {
            return new ProviderLink(ProviderUri, ProviderPatientId);
        }
    }
}
