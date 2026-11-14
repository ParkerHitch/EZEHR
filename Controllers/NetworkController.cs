using System.Security.Cryptography.X509Certificates;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using Microsoft.AspNetCore.Mvc;

namespace EZEHR.Controllers;

/// <summary>
/// The network door: organizations and apps call these endpoints over mutual TLS with EZCA certificates.
/// 401 means the certificate itself was not accepted; 403 means a verified caller isn't allowed this action.
/// </summary>
[ApiController]
[Route("api/network")]
public sealed class NetworkController(
    IClientCertificateService clientCertificateService,
    CallerManager callerManager,
    InviteManager inviteManager,
    RecordManager recordManager
) : ControllerBase
{
    [HttpPost("invites")]
    public async Task<IActionResult> CreateInviteAsync(InviteRequest request)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromCertificate(certificate);
        CreateInviteResult result = await inviteManager.CreateAsync(
            caller,
            request.ProviderPatientId
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return StatusCode(
            StatusCodes.Status201Created,
            new InviteResponse(result.Code!, result.Url!)
        );
    }

    [HttpPost("records")]
    public async Task<IActionResult> IngestAsync(IngestRequest request)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromCertificate(certificate);
        IngestResult result = await recordManager.IngestAsync(
            caller,
            request.ProviderPatientId,
            request.Entries
        );
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        if (result.Error is RecordError error)
        {
            return RecordErrorResult(error);
        }
        return Ok(new IngestResponse(result.Inserted, result.Skipped));
    }

    [HttpGet("patients/{patientId:guid}/labs")]
    public async Task<IActionResult> ReadLabsAsync(Guid patientId)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromCertificate(certificate);
        LabsResult result = await recordManager.ReadLabsAsync(patientId, caller);
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(new EntriesResponse(result.Labs));
    }

    [HttpGet("patients/{patientId:guid}/record")]
    public async Task<IActionResult> ReadRecordAsync(Guid patientId, string? purpose)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromCertificate(certificate);
        RecordResult result = await recordManager.ReadRecordAsync(patientId, caller, purpose);
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        return Ok(new EntriesResponse(result.Entries));
    }

    [HttpPost("patients/{patientId:guid}/notes")]
    public async Task<IActionResult> WriteNoteAsync(Guid patientId, NoteRequest request)
    {
        X509Certificate2? certificate = await clientCertificateService.GetAsync(HttpContext);
        Caller caller = callerManager.FromCertificate(certificate);
        NoteResult result = await recordManager.WriteNoteAsync(patientId, caller, request.Jws);
        if (!result.Allowed)
        {
            return DenialResult(result.Reason);
        }
        if (result.Error is RecordError error)
        {
            return RecordErrorResult(error);
        }
        return Ok(new NoteResponse(result.Inserted, result.EntryId));
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
