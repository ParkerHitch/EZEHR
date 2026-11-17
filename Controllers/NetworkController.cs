using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EZEHR.Controllers;

/// <summary>
/// The network door: organizations and apps call these endpoints over mutual TLS with EZCA certificates.
/// 401 means the certificate itself was not accepted; 403 means a verified caller isn't allowed this action.
/// </summary>
[ApiController]
[Route("api/network")]
[EnableRateLimiting(RateLimitPolicy)]
public sealed class NetworkController(
    IClientCertificateService clientCertificateService,
    CallerManager callerManager,
    InviteManager inviteManager,
    RecordManager recordManager,
    OrganizationAuditManager organizationAuditManager,
    OrganizationManager organizationManager,
    LaunchManager launchManager
) : ControllerBase
{
    /// <summary>Calls a minute per client address, set up in Program.cs.</summary>
    public const string RateLimitPolicy = "network";

    /// <summary>
    /// The platform operator adds a provider to the organizations table, with the pinned platform certificate. The
    /// provider's EZCA certificate (CN {slug}.providers.ezehr) is accepted from the next request.
    /// </summary>
    [HttpPost("register-provider")]
    public async Task<IActionResult> RegisterProviderAsync(RegisterProviderRequest request)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromPlatformCertificate(certificate);
        RegisterProviderResult result = await organizationManager.RegisterProviderAsync(
            caller,
            request.Slug,
            request.DisplayName
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        if (result.Error is OrganizationError error)
        {
            return Conflict(new ErrorResponse(error.ToString()));
        }
        return StatusCode(
            StatusCodes.Status201Created,
            new RegisterProviderResponse(
                result.Organization!.Uri,
                result.Organization.CertificateCommonName
            )
        );
    }

    /// <summary>
    /// The platform operator adds an app to the organizations table, with the pinned platform certificate. Patients
    /// then see it on their Access page, off until they turn it on.
    /// </summary>
    [HttpPost("register-app")]
    public async Task<IActionResult> RegisterAppAsync(RegisterAppRequest request)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromPlatformCertificate(certificate);
        RegisterAppResult result = await organizationManager.RegisterAppAsync(
            caller,
            request.Slug,
            request.DisplayName,
            request.Scope!.Value,
            request.Description
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        if (result.Error == OrganizationError.AppScopeNotAllowed)
        {
            return BadRequest(new ErrorResponse(result.Error.Value.ToString()));
        }
        if (result.Error is OrganizationError error)
        {
            return Conflict(new ErrorResponse(error.ToString()));
        }
        return StatusCode(
            StatusCodes.Status201Created,
            new RegisterAppResponse(
                result.Organization!.Uri,
                result.Organization.CertificateCommonName
            )
        );
    }

    [HttpPost("create-enrollment-invite")]
    public async Task<IActionResult> CreateEnrollmentInviteAsync(
        CreateEnrollmentInviteRequest request
    )
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        CreateEnrollmentInviteResult result = await inviteManager.CreateEnrollmentInviteAsync(
            caller,
            request.ProviderPatientId
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return StatusCode(
            StatusCodes.Status201Created,
            new CreateEnrollmentInviteResponse(result.InviteCode!, result.EnrollmentUrl!)
        );
    }

    /// <summary>
    /// A provider's system opens EZEHR for one of its own clinicians, from one patient's chart, for one visit. The
    /// clinician can only ask for a record through the link this returns.
    /// </summary>
    [HttpPost("create-clinician-launch")]
    public async Task<IActionResult> CreateClinicianLaunchAsync(
        CreateClinicianLaunchRequest request
    )
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        CreateClinicianLaunchResult result = await launchManager.CreateClinicianLaunchAsync(
            caller,
            request.ClinicianObjectId!.Value,
            request.ProviderPatientId,
            request.EncounterId
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        if (result.Error is LaunchError error)
        {
            return Conflict(new ErrorResponse(error.ToString()));
        }
        return StatusCode(
            StatusCodes.Status201Created,
            new CreateClinicianLaunchResponse(result.LaunchCode!, result.LaunchUrl!)
        );
    }

    [HttpPost("push-signed-records")]
    public async Task<IActionResult> PushSignedRecordsAsync(PushSignedRecordsRequest request)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        PushSignedRecordsResult result = await recordManager.PushSignedRecordsAsync(
            caller,
            request.ProviderPatientId,
            request.SignedEntries
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        if (result.Error is RecordError error)
        {
            return RecordErrorResult(error);
        }
        return Ok(
            new PushSignedRecordsResponse(result.Stored, result.AlreadyStored, result.Replaced)
        );
    }

    [HttpGet("patients/{patientId:guid}/read-egfr-labs")]
    public async Task<IActionResult> ReadEgfrLabsAsync(Guid patientId)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        ReadEgfrLabsResult result = await recordManager.ReadEgfrLabsAsync(patientId, caller);
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(new SignedEntriesResponse(result.EgfrLabs));
    }

    [HttpGet("patients/{patientId:guid}/read-medications")]
    public async Task<IActionResult> ReadMedicationsAsync(Guid patientId)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        ReadMedicationsResult result = await recordManager.ReadMedicationsAsync(patientId, caller);
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(new SignedEntriesResponse(result.Medications));
    }

    [HttpGet("patients/{patientId:guid}/read-full-record")]
    public async Task<IActionResult> ReadFullRecordAsync(
        Guid patientId,
        [FromQuery(Name = "purpose")] string? claimedPurpose
    )
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        ReadFullRecordResult result = await recordManager.ReadFullRecordAsync(
            patientId,
            caller,
            claimedPurpose
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(new SignedEntriesResponse(result.Entries));
    }

    [HttpPost("patients/{patientId:guid}/write-signed-note")]
    public async Task<IActionResult> WriteSignedNoteAsync(
        Guid patientId,
        WriteSignedNoteRequest request
    )
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        WriteSignedNoteResult result = await recordManager.WriteSignedNoteAsync(
            patientId,
            caller,
            request.Jws
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        if (result.Error is RecordError error)
        {
            return RecordErrorResult(error);
        }
        return Ok(new WriteSignedNoteResponse(result.Stored, result.EntryId));
    }

    /// <summary>
    /// The caller organization's own audit trail: attempts by its clinicians and its own system, oldest first.
    /// Filters combine; page with the response's nextAfterId.
    /// </summary>
    [HttpGet("read-organization-audit")]
    public async Task<IActionResult> ReadOrganizationAuditAsync(
        [FromQuery] string? clinicianUri,
        [FromQuery] Guid? patientId,
        [FromQuery] string? providerPatientId,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] DateTimeOffset? until,
        [FromQuery] long afterId = 0,
        [FromQuery, Range(1, 500)] int limit = 100
    )
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        ReadOrganizationAuditResult result =
            await organizationAuditManager.ReadOrganizationAuditAsync(
                caller,
                new OrganizationAuditFilter(
                    clinicianUri,
                    patientId,
                    providerPatientId,
                    since,
                    until,
                    afterId,
                    limit
                )
            );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(new ReadOrganizationAuditResponse(result.Events, result.NextAfterId));
    }

    /// <summary>The clinicians who have acted for the caller organization, most recently seen first.</summary>
    [HttpGet("list-organization-clinicians")]
    public async Task<IActionResult> ListOrganizationCliniciansAsync()
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = await callerManager.FromCertificateAsync(certificate);
        ListOrganizationCliniciansResult result =
            await organizationAuditManager.ListOrganizationCliniciansAsync(caller);
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(new ListOrganizationCliniciansResponse(result.Clinicians));
    }

    /// <summary>401 for certificate problems; 403 for everything else a verified caller is refused.</summary>
    private ObjectResult DenialResult(DenyReason? reason)
    {
        int status = reason
            is DenyReason.NoCertificate
                or DenyReason.UntrustedCertificate
                or DenyReason.CertificateExpired
                or DenyReason.NotSignedIn
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;
        return StatusCode(status, new ErrorResponse(reason.ToString()!));
    }

    /// <summary>409 when the provider's patient isn't linked yet; 400 for records that fail their checks.</summary>
    private ObjectResult RecordErrorResult(RecordError error)
    {
        int status =
            error == RecordError.PatientNotLinked
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
        return StatusCode(status, new ErrorResponse(error.ToString()));
    }
}
