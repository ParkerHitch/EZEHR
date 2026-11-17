using Dapper;
using EZEHR.Data;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EZEHR.Managers;

/// <summary>
/// Launches: how a clinician's organization takes part in each access. The organization's own system (over the
/// network door, with its certificate) says "this clinician of ours is opening this patient's chart for this
/// visit" and gets a link. The clinician opens the link, signed in as themselves, and only then can ask for a
/// record. No launch, no request: the organization can refuse simply by not making one.
/// </summary>
public sealed class LaunchManager(
    Db db,
    OrganizationManager organizationManager,
    TimeProvider time,
    IOptions<AppOptions> options
)
{
    /// <summary>How long a launch link can wait to be opened. It is made at the moment the clinician clicks.</summary>
    public static readonly TimeSpan LaunchOpenWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an opened launch can be used to ask for a record. It bounds new requests only: access the patient
    /// gives lasts as long as the patient chose.
    /// </summary>
    public static readonly TimeSpan LaunchLifetime = TimeSpan.FromHours(1);

    private const string LaunchColumns = """
        id AS Id, provider_uri AS ProviderUri, clinician_uri AS ClinicianUri,
        provider_patient_id AS ProviderPatientId, encounter_id AS EncounterId, ends_at AS EndsAt
        """;

    /// <summary>
    /// A provider's system makes a launch for one of its own clinicians. Only verified providers may, and only for
    /// the Entra tenant on their own row, so a provider can never launch another organization's clinician.
    /// </summary>
    public async Task<CreateClinicianLaunchResult> CreateClinicianLaunchAsync(
        Caller caller,
        Guid clinicianObjectId,
        string providerPatientId,
        string encounterId
    )
    {
        if (caller is UnverifiedCaller unverified)
        {
            return new CreateClinicianLaunchResult(false, unverified.Reason, null, null, null);
        }
        if (caller is not VerifiedCaller { Party.Kind: PartyKind.Provider } provider)
        {
            return new CreateClinicianLaunchResult(
                false,
                DenyReason.WrongPartyKind,
                null,
                null,
                null
            );
        }
        OrganizationDirectory directory = await organizationManager.LoadDirectoryAsync();
        if (directory.FindTenantOfProvider(provider.Party.SubjectUri) is not Guid tenantId)
        {
            return new CreateClinicianLaunchResult(
                true,
                null,
                LaunchError.ProviderHasNoClinicians,
                null,
                null
            );
        }

        string code = GrantManager.GenerateCode();
        DateTimeOffset now = time.GetUtcNow();
        await using SqlConnection connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO clinician_launches(id, code, provider_uri, clinician_uri, provider_patient_id,
                                           encounter_id, created_at, open_by)
            VALUES (@id, @code, @providerUri, @clinicianUri, @providerPatientId, @encounterId, @now, @openBy)
            """,
            new
            {
                id = Guid.NewGuid(),
                code,
                providerUri = provider.Party.SubjectUri,
                clinicianUri = OrgDirectory.ClinicianUri(
                    tenantId.ToString(),
                    clinicianObjectId.ToString()
                ),
                providerPatientId,
                encounterId,
                now,
                openBy = now + LaunchOpenWindow,
            }
        );
        string launchUrl = $"{options.Value.Origin.TrimEnd('/')}/Clinician/Launch?launch={code}";
        return new CreateClinicianLaunchResult(true, null, null, code, launchUrl);
    }

    /// <summary>
    /// The signed-in clinician opens a launch link. It opens only for the clinician the organization named, and
    /// only within a few minutes of being made. Opening it again while it is still running gives the same launch,
    /// so a reload or the back button doesn't strand the clinician.
    /// </summary>
    public async Task<OpenClinicianLaunchResult> OpenClinicianLaunchAsync(
        string? launchCode,
        Party clinician
    )
    {
        string normalizedCode = GrantManager.NormalizeCode(launchCode);
        DateTimeOffset now = time.GetUtcNow();
        await using SqlConnection connection = await db.OpenAsync();
        // The conditional UPDATE is the claim, as for share codes.
        ClinicianLaunch? opened = await connection.QuerySingleOrDefaultAsync<ClinicianLaunch>(
            """
            UPDATE clinician_launches SET opened_at = @now, ends_at = @endsAt
            OUTPUT inserted.id AS Id, inserted.provider_uri AS ProviderUri,
                   inserted.clinician_uri AS ClinicianUri,
                   inserted.provider_patient_id AS ProviderPatientId,
                   inserted.encounter_id AS EncounterId, inserted.ends_at AS EndsAt
            WHERE code = @normalizedCode AND clinician_uri = @clinicianUri AND opened_at IS NULL
                  AND open_by >= @now
            """,
            new
            {
                now,
                endsAt = now + LaunchLifetime,
                normalizedCode,
                clinicianUri = clinician.SubjectUri,
            }
        );
        if (opened is not null)
        {
            return new OpenClinicianLaunchResult(true, opened, null);
        }

        string? madeFor = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT clinician_uri FROM clinician_launches WHERE code = @normalizedCode",
            new { normalizedCode }
        );
        if (madeFor is null)
        {
            return new OpenClinicianLaunchResult(false, null, LaunchError.LaunchUnknown);
        }
        if (madeFor != clinician.SubjectUri)
        {
            return new OpenClinicianLaunchResult(
                false,
                null,
                LaunchError.LaunchForAnotherClinician
            );
        }
        ClinicianLaunch? stillOpen = await connection.QuerySingleOrDefaultAsync<ClinicianLaunch>(
            $"""
            SELECT {LaunchColumns}
            FROM clinician_launches
            WHERE code = @normalizedCode AND opened_at IS NOT NULL AND ends_at > @now
            """,
            new { normalizedCode, now }
        );
        if (stillOpen is null)
        {
            return new OpenClinicianLaunchResult(false, null, LaunchError.LaunchExpired);
        }
        return new OpenClinicianLaunchResult(true, stillOpen, null);
    }

    /// <summary>The clinician's own launch, if it has been opened and hasn't run out. Null for anyone else's.</summary>
    public async Task<ClinicianLaunch?> FindOpenLaunchAsync(Guid launchId, Party clinician)
    {
        await using SqlConnection connection = await db.OpenAsync();
        return await connection.QuerySingleOrDefaultAsync<ClinicianLaunch>(
            $"""
            SELECT {LaunchColumns}
            FROM clinician_launches
            WHERE id = @launchId AND clinician_uri = @clinicianUri AND opened_at IS NOT NULL
                  AND ends_at > @now
            """,
            new
            {
                launchId,
                clinicianUri = clinician.SubjectUri,
                now = time.GetUtcNow(),
            }
        );
    }
}
