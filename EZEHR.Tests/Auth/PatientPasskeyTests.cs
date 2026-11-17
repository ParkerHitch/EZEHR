using Dapper;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tests.Data;
using Fido2NetLib;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace EZEHR.Tests.Auth;

public class PatientPasskeyTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(TestPki.Start);
    private readonly FakeFido2Service _fido2 = new();
    private TestDatabase _database = null!;
    private TestServices _services = null!;
    private EnrollmentManager _enrollment = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServer.CreateDatabaseAsync();
        _services = new TestServices(_database.Db, _clock);
        _enrollment = new EnrollmentManager(
            _database.Db,
            _fido2,
            _services.Invites,
            _services.Records,
            _services.Grants,
            _services.Organizations,
            _clock
        );
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private async Task<string> InviteAsync(string slug, string providerPatientId)
    {
        Party provider = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri(slug))!;
        CreateEnrollmentInviteResult invite = await _services.Invites.CreateEnrollmentInviteAsync(
            new VerifiedCaller(provider, null),
            providerPatientId
        );
        return invite.InviteCode!;
    }

    /// <summary>Runs both registration steps the way the phone would, with the fake accepting the passkey.</summary>
    private async Task<EnrollmentResult> EnrollAsync(string inviteCode, bool kidneyCheckOptIn)
    {
        CreateRegistrationOptionsResult options = await _enrollment.CreateRegistrationOptionsAsync(
            inviteCode,
            "Sam Taylor"
        );
        if (!options.Succeeded)
        {
            return new EnrollmentResult(false, null, options.Error);
        }
        _fido2.NextRegistration = FakeFido2Service.NewPasskey(options.Options!);
        return await _enrollment.CompleteRegistrationAsync(
            inviteCode,
            kidneyCheckOptIn ? [OrgDirectory.KidneyCheckUri] : [],
            new AuthenticatorAttestationRawResponse(),
            options.Options!
        );
    }

    private async Task<T> QueryAsync<T>(string sql, object? parameters = null)
    {
        await using SqlConnection connection = await _database.Db.OpenAsync();
        return await connection.QuerySingleAsync<T>(sql, parameters);
    }

    [Fact]
    public async Task Invite_single_use()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");

        EnrollmentResult first = await EnrollAsync(code, false);
        EnrollmentResult second = await EnrollAsync(code, false);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(EnrollmentError.InviteUsed, second.Error);
        Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM patients"));
        Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM patient_credentials"));
    }

    [Fact]
    public async Task Invite_used_between_options_and_completion_creates_nothing()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");
        CreateRegistrationOptionsResult options = await _enrollment.CreateRegistrationOptionsAsync(
            code,
            "Sam Taylor"
        );
        Assert.True((await EnrollAsync(code, false)).Succeeded);

        _fido2.NextRegistration = FakeFido2Service.NewPasskey(options.Options!);
        EnrollmentResult late = await _enrollment.CompleteRegistrationAsync(
            code,
            [],
            new AuthenticatorAttestationRawResponse(),
            options.Options!
        );

        Assert.Equal(EnrollmentError.InviteUsed, late.Error);
        Assert.Equal(1, await QueryAsync<int>("SELECT COUNT(*) FROM patients"));
    }

    [Fact]
    public async Task Invite_expired()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");
        _clock.Advance(InviteManager.InviteLifetime + TimeSpan.FromSeconds(1));

        EnrollmentResult result = await EnrollAsync(code, false);

        Assert.Equal(EnrollmentError.InviteExpired, result.Error);
    }

    [Fact]
    public async Task Unknown_invite_gets_no_options()
    {
        CreateRegistrationOptionsResult result = await _enrollment.CreateRegistrationOptionsAsync(
            "ZZZZ-ZZZZ",
            "Sam Taylor"
        );

        Assert.False(result.Succeeded);
        Assert.Equal(EnrollmentError.InviteUnknown, result.Error);
    }

    [Fact]
    public async Task Rejected_passkey_leaves_invite_usable()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");
        CreateRegistrationOptionsResult options = await _enrollment.CreateRegistrationOptionsAsync(
            code,
            "Sam Taylor"
        );
        _fido2.NextRegistration = null;

        EnrollmentResult rejected = await _enrollment.CompleteRegistrationAsync(
            code,
            [],
            new AuthenticatorAttestationRawResponse(),
            options.Options!
        );

        Assert.Equal(EnrollmentError.PasskeyRejected, rejected.Error);
        Assert.True((await EnrollAsync(code, false)).Succeeded);
    }

    [Fact]
    public async Task OptIn_creates_app_grant()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");

        EnrollmentResult result = await EnrollAsync(code, true);

        IReadOnlyList<Grant> grants = await _services.Grants.ListForPatientAsync(
            result.PatientId!.Value
        );
        Grant grant = Assert.Single(grants);
        Assert.Equal("urn:app:kidney-check", grant.Grantee.SubjectUri);
        Assert.Equal(GrantScope.LabsAndNotes, grant.Scope);
        Assert.Equal(GrantStatus.Active, grant.Status);
    }

    [Fact]
    public async Task Each_ticked_app_gets_its_own_scope_and_anything_else_is_ignored()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");
        CreateRegistrationOptionsResult options = await _enrollment.CreateRegistrationOptionsAsync(
            code,
            "Sam Taylor"
        );
        _fido2.NextRegistration = FakeFido2Service.NewPasskey(options.Options!);

        EnrollmentResult result = await _enrollment.CompleteRegistrationAsync(
            code,
            [
                OrgDirectory.MedicationCheckUri,
                OrgDirectory.KidneyCheckUri,
                OrgDirectory.MedicationCheckUri,
                "urn:provider:riverside",
                "urn:app:nobody",
            ],
            new AuthenticatorAttestationRawResponse(),
            options.Options!
        );

        IReadOnlyList<Grant> grants = await _services.Grants.ListForPatientAsync(
            result.PatientId!.Value
        );
        Assert.Equal(2, grants.Count);
        Assert.Equal(
            GrantScope.MedicationsLabsAndNotes,
            grants
                .Single(grant => grant.Grantee.SubjectUri == OrgDirectory.MedicationCheckUri)
                .Scope
        );
        Assert.Equal(
            GrantScope.LabsAndNotes,
            grants.Single(grant => grant.Grantee.SubjectUri == OrgDirectory.KidneyCheckUri).Scope
        );
    }

    [Fact]
    public async Task Invite_check_lists_the_apps_a_new_patient_can_turn_on()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");

        InviteCheck usable = await _enrollment.CheckInviteAsync(code);
        InviteCheck unknown = await _enrollment.CheckInviteAsync("NOPE-NOPE");

        Assert.Equal(["Kidney Check", "Medication Check"], usable.Apps.Select(app => app.Name));
        Assert.All(usable.Apps, app => Assert.Null(app.Grant));
        Assert.Empty(unknown.Apps);
    }

    [Fact]
    public async Task No_optin_no_grant()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");

        EnrollmentResult result = await EnrollAsync(code, false);

        Assert.Empty(await _services.Grants.ListForPatientAsync(result.PatientId!.Value));
    }

    [Fact]
    public async Task Enroll_creates_wrapped_dek_and_link()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");

        EnrollmentResult result = await EnrollAsync(code, false);

        byte[] wrappedDataKey = await QueryAsync<byte[]>(
            "SELECT wrapped_dek FROM patients WHERE id = @patientId",
            new { patientId = result.PatientId }
        );
        Assert.True(wrappedDataKey.Length > 32);
        Assert.Equal(
            result.PatientId,
            await _services.Invites.FindLinkedPatientAsync("urn:provider:lakeside", "LUC-20417")
        );
        Assert.Equal(
            "Sam Taylor",
            await QueryAsync<string>(
                "SELECT display_name FROM patients WHERE id = @patientId",
                new { patientId = result.PatientId }
            )
        );
    }

    [Fact]
    public async Task Redeem_second_invite_adds_link()
    {
        EnrollmentResult sam = await EnrollAsync(await InviteAsync("lakeside", "LUC-20417"), false);
        string northwind = await InviteAsync("northwind", "NWH-88213");

        EnrollmentResult linked = await _enrollment.LinkInviteAsync(
            sam.PatientId!.Value,
            northwind
        );

        Assert.True(linked.Succeeded);
        Assert.Equal(
            sam.PatientId,
            await _services.Invites.FindLinkedPatientAsync("urn:provider:northwind", "NWH-88213")
        );
        Assert.Equal(EnrollmentError.InviteUsed, (await EnrollAsync(northwind, false)).Error);
    }

    [Fact]
    public async Task Connected_providers_lists_each_link_oldest_first_with_its_day()
    {
        EnrollmentResult sam = await EnrollAsync(await InviteAsync("lakeside", "LUC-20417"), false);
        _clock.Advance(TimeSpan.FromDays(1));
        await _enrollment.LinkInviteAsync(
            sam.PatientId!.Value,
            await InviteAsync("northwind", "NWH-88213")
        );

        IReadOnlyList<ConnectedProviderLine> providers =
            await _services.Invites.ListConnectedProvidersAsync(sam.PatientId!.Value);

        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(new AppOptions().TimeZone);
        Assert.Equal(
            [
                new ConnectedProviderLine(
                    "urn:provider:lakeside",
                    Copy.ConnectedProviderLine(
                        "Lakeside Urgent Care",
                        LocalTime.Day(TestPki.Start, zone)
                    )
                ),
                new ConnectedProviderLine(
                    "urn:provider:northwind",
                    Copy.ConnectedProviderLine(
                        "Northwind Workplace Health",
                        LocalTime.Day(TestPki.Start.AddDays(1), zone)
                    )
                ),
            ],
            providers
        );
    }

    [Fact]
    public async Task Disconnect_removes_only_the_patients_own_link()
    {
        EnrollmentResult sam = await EnrollAsync(await InviteAsync("lakeside", "LUC-20417"), false);
        EnrollmentResult other = await EnrollAsync(
            await InviteAsync("northwind", "NWH-11111"),
            false
        );

        await _services.Invites.DisconnectProviderAsync(
            other.PatientId!.Value,
            "urn:provider:lakeside"
        );
        Assert.Equal(
            sam.PatientId,
            await _services.Invites.FindLinkedPatientAsync("urn:provider:lakeside", "LUC-20417")
        );

        await _services.Invites.DisconnectProviderAsync(
            sam.PatientId!.Value,
            "urn:provider:lakeside"
        );
        Assert.Null(
            await _services.Invites.FindLinkedPatientAsync("urn:provider:lakeside", "LUC-20417")
        );
        Assert.Empty(await _services.Invites.ListConnectedProvidersAsync(sam.PatientId!.Value));
    }

    [Fact]
    public async Task Invite_check_names_the_provider()
    {
        string code = await InviteAsync("maple", "MSF-0932");

        InviteCheck check = await _enrollment.CheckInviteAsync(GrantManager.FormatCode(code));

        Assert.True(check.Usable);
        Assert.Equal("Maple Street Family Medicine", check.ProviderName);
    }

    [Fact]
    public async Task Sign_in_with_enrolled_passkey_returns_patient_and_updates_counter()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");
        CreateRegistrationOptionsResult options = await _enrollment.CreateRegistrationOptionsAsync(
            code,
            "Sam Taylor"
        );
        RegisteredPasskey passkey = FakeFido2Service.NewPasskey(options.Options!);
        _fido2.NextRegistration = passkey;
        EnrollmentResult sam = await _enrollment.CompleteRegistrationAsync(
            code,
            [],
            new AuthenticatorAttestationRawResponse(),
            options.Options!
        );
        _fido2.NextSignInCounter = 7;

        EnrollmentResult signIn = await _enrollment.CompleteSignInAsync(
            Assertion(passkey.CredentialId),
            _enrollment.CreateSignInOptions()
        );

        Assert.True(signIn.Succeeded);
        Assert.Equal(sam.PatientId, signIn.PatientId);
        Assert.Equal(passkey.UserHandle, _fido2.LastCheckedPasskey!.UserHandle);
        Assert.Equal(7, await QueryAsync<long>("SELECT sign_count FROM patient_credentials"));
    }

    [Fact]
    public async Task Sign_in_with_unknown_passkey_fails()
    {
        _fido2.NextSignInCounter = 1;

        EnrollmentResult signIn = await _enrollment.CompleteSignInAsync(
            Assertion(Guid.NewGuid().ToByteArray()),
            _enrollment.CreateSignInOptions()
        );

        Assert.Equal(EnrollmentError.PasskeyUnknown, signIn.Error);
        Assert.Null(_fido2.LastCheckedPasskey);
    }

    [Fact]
    public async Task Sign_in_with_bad_signature_fails()
    {
        string code = await InviteAsync("lakeside", "LUC-20417");
        CreateRegistrationOptionsResult options = await _enrollment.CreateRegistrationOptionsAsync(
            code,
            "Sam Taylor"
        );
        RegisteredPasskey passkey = FakeFido2Service.NewPasskey(options.Options!);
        _fido2.NextRegistration = passkey;
        await _enrollment.CompleteRegistrationAsync(
            code,
            [],
            new AuthenticatorAttestationRawResponse(),
            options.Options!
        );
        _fido2.NextSignInCounter = null;

        EnrollmentResult signIn = await _enrollment.CompleteSignInAsync(
            Assertion(passkey.CredentialId),
            _enrollment.CreateSignInOptions()
        );

        Assert.Equal(EnrollmentError.PasskeyRejected, signIn.Error);
    }

    private static AuthenticatorAssertionRawResponse Assertion(byte[] credentialId)
    {
        return new AuthenticatorAssertionRawResponse
        {
            Id = Convert.ToBase64String(credentialId),
            RawId = credentialId,
        };
    }

    [Fact]
    public async Task Opt_in_after_kidney_check_was_removed_enrolls_without_a_grant()
    {
        await using (SqlConnection connection = await _database.Db.OpenAsync())
        {
            await connection.ExecuteAsync(
                "DELETE FROM organizations WHERE uri = 'urn:app:kidney-check'"
            );
        }
        string code = await InviteAsync("lakeside", "LUC-20417");

        EnrollmentResult result = await EnrollAsync(code, true);

        Assert.True(result.Succeeded);
        Assert.Empty(await _services.Grants.ListForPatientAsync(result.PatientId!.Value));
    }
}
