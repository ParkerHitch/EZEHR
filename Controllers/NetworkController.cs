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
    RecordManager recordManager
) : ControllerBase
{
    /// <summary>Calls a minute per client address, set up in Program.cs.</summary>
    public const string RateLimitPolicy = "network";

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
