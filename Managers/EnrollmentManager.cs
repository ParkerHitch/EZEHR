using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Fido2NetLib;
using Microsoft.Data.SqlClient;

namespace EZEHR.Managers;

/// <summary>
/// Patient accounts: enrollment from a provider invite with a new passkey, linking more providers' invites,
/// and passkey sign-in. Nothing is shared at enrollment except grants for the apps the patient ticks.
/// </summary>
public sealed class EnrollmentManager(
    Db db,
    IFido2Service fido2Service,
    InviteManager inviteManager,
    RecordManager recordManager,
    GrantManager grantManager,
    OrganizationManager organizationManager,
    TimeProvider time
)
{
    public const int MaxDisplayNameLength = 200;

    /// <summary>Looks at an invite without using it, so the page can say who sent it.</summary>
    public async Task<InviteCheck> CheckInviteAsync(string inviteCode)
    {
        InviteResult invite = await inviteManager.CheckInviteAsync(inviteCode);
        if (!invite.Succeeded)
        {
            return new InviteCheck(false, null, ToEnrollmentError(invite.Error!.Value), []);
        }
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        List<AppLine> apps = directory
            .ListApps()
            .Select(app => new AppLine(
                app.Uri,
                app.DisplayName,
                app.AppDescription,
                Copy.AppScopeWhat(app.GrantedAppScope()),
                null
            ))
            .ToList();
        return new InviteCheck(true, await ProviderNameAsync(invite.Link!), null, apps);
    }

    /// <summary>Step 1 of enrollment: a WebAuthn challenge, only for an invite that can still be used.</summary>
    public async Task<CreateRegistrationOptionsResult> CreateRegistrationOptionsAsync(
        string inviteCode,
        string displayName
    )
    {
        InviteResult invite = await inviteManager.CheckInviteAsync(inviteCode);
        if (!invite.Succeeded)
        {
            return new CreateRegistrationOptionsResult(
                false,
                null,
                ToEnrollmentError(invite.Error!.Value)
            );
        }

        string boundedName = displayName.Trim();
        if (boundedName.Length > MaxDisplayNameLength)
        {
            boundedName = boundedName[..MaxDisplayNameLength];
        }
        // The user handle is random, not the patient id: it is sent to the phone and kept by its passkey manager.
        Fido2User user = new()
        {
            Id = RandomNumberGenerator.GetBytes(32),
            Name = boundedName,
            DisplayName = boundedName,
        };
        CredentialCreateOptions options = fido2Service.CreateRegistrationOptions(user);
        return new CreateRegistrationOptionsResult(true, options, null);
    }

    /// <summary>
    /// Step 2 of enrollment. Verifies the new passkey, then in one transaction claims the invite, creates the
    /// patient with a freshly wrapped data key, and stores the passkey's public key. A used invite creates nothing.
    /// Then turns on each app the patient ticked; a URI that isn't an app on the network is ignored.
    /// </summary>
    public async Task<EnrollmentResult> CompleteRegistrationAsync(
        string inviteCode,
        IReadOnlyList<string> appUris,
        AuthenticatorAttestationRawResponse attestation,
        CredentialCreateOptions originalOptions
    )
    {
        InviteResult invite = await inviteManager.CheckInviteAsync(inviteCode);
        if (!invite.Succeeded)
        {
            return Failed(ToEnrollmentError(invite.Error!.Value));
        }
        RegisteredPasskey? passkey = await fido2Service.VerifyRegistrationAsync(
            attestation,
            originalOptions
        );
        if (passkey is null)
        {
            return Failed(EnrollmentError.PasskeyRejected);
        }

        Guid patientId = Guid.NewGuid();
        // Wrapping is a Key Vault call, so it happens before the transaction opens.
        byte[] wrappedDataKey = await recordManager.CreatePatientKeyAsync();
        await using SqlConnection connection = await db.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        InviteResult redeemed = await inviteManager.RedeemInviteAsync(
            connection,
            transaction,
            inviteCode,
            patientId
        );
        if (!redeemed.Succeeded)
        {
            return Failed(ToEnrollmentError(redeemed.Error!.Value));
        }
        await connection.ExecuteAsync(
            """
            INSERT INTO patients(id, display_name, wrapped_dek, created_at)
            VALUES (@patientId, @displayName, @wrappedDataKey, @createdAt)
            """,
            new
            {
                patientId,
                displayName = originalOptions.User.DisplayName,
                wrappedDataKey,
                createdAt = time.GetUtcNow(),
            },
            transaction
        );
        await connection.ExecuteAsync(
            """
            INSERT INTO patient_credentials(credential_id, patient_id, public_key, sign_count, user_handle)
            VALUES (@CredentialId, @patientId, @PublicKey, @signCount, @UserHandle)
            """,
            new
            {
                passkey.CredentialId,
                patientId,
                passkey.PublicKey,
                signCount = (long)passkey.SignCount,
                passkey.UserHandle,
            },
            transaction
        );
        await transaction.CommitAsync();

        foreach (string appUri in appUris.Distinct())
        {
            await grantManager.TurnOnAppAsync(patientId, appUri);
        }
        return new EnrollmentResult(true, patientId, null);
    }

    /// <summary>A signed-in patient redeems another provider's invite, adding that provider to the record.</summary>
    public async Task<EnrollmentResult> LinkInviteAsync(Guid patientId, string inviteCode)
    {
        InviteResult redeemed = await inviteManager.RedeemInviteAsync(inviteCode, patientId);
        if (!redeemed.Succeeded)
        {
            return Failed(ToEnrollmentError(redeemed.Error!.Value));
        }
        return new EnrollmentResult(true, patientId, null);
    }

    public AssertionOptions CreateSignInOptions()
    {
        return fido2Service.CreateSignInOptions();
    }

    /// <summary>Usernameless sign-in: the passkey says which credential it is, and its signature proves it.</summary>
    public async Task<EnrollmentResult> CompleteSignInAsync(
        AuthenticatorAssertionRawResponse assertion,
        AssertionOptions originalOptions
    )
    {
        await using SqlConnection connection = await db.OpenAsync();
        StoredPasskeyRow? row = await connection.QuerySingleOrDefaultAsync<StoredPasskeyRow>(
            """
            SELECT credential_id AS CredentialId, patient_id AS PatientId, public_key AS PublicKey,
                   sign_count AS SignCount, user_handle AS UserHandle
            FROM patient_credentials
            WHERE credential_id = @credentialId
            """,
            new { credentialId = assertion.RawId }
        );
        if (row is null)
        {
            return Failed(EnrollmentError.PasskeyUnknown);
        }

        StoredPasskey stored = row.ToStoredPasskey();
        uint? signCount = await fido2Service.VerifySignInAsync(assertion, originalOptions, stored);
        if (signCount is null)
        {
            return Failed(EnrollmentError.PasskeyRejected);
        }
        await connection.ExecuteAsync(
            "UPDATE patient_credentials SET sign_count = @signCount WHERE credential_id = @credentialId",
            new { signCount = (long)signCount.Value, credentialId = stored.CredentialId }
        );
        return new EnrollmentResult(true, stored.PatientId, null);
    }

    private async Task<string> ProviderNameAsync(ProviderLink link)
    {
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        return directory.FindByUri(link.ProviderUri)?.DisplayName ?? link.ProviderUri;
    }

    private static EnrollmentError ToEnrollmentError(InviteError error)
    {
        return error switch
        {
            InviteError.InviteUnknown => EnrollmentError.InviteUnknown,
            InviteError.InviteUsed => EnrollmentError.InviteUsed,
            InviteError.InviteExpired => EnrollmentError.InviteExpired,
            InviteError.InviteLinkedElsewhere => EnrollmentError.InviteLinkedElsewhere,
            _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
        };
    }

    private static EnrollmentResult Failed(EnrollmentError error)
    {
        return new EnrollmentResult(false, null, error);
    }

    // sign_count is BIGINT, so it is read as long and narrowed once here.
    private sealed record StoredPasskeyRow(
        byte[] CredentialId,
        Guid PatientId,
        byte[] PublicKey,
        long SignCount,
        byte[] UserHandle
    )
    {
        public StoredPasskey ToStoredPasskey()
        {
            return new StoredPasskey(
                CredentialId,
                PatientId,
                PublicKey,
                (uint)SignCount,
                UserHandle
            );
        }
    }
}
