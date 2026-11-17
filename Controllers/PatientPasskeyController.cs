using System.Text.Json;
using EZEHR.Managers;
using EZEHR.Models;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace EZEHR.Controllers;

/// <summary>
/// The patient passkey ceremonies that passkey.js drives. Each ceremony's options (its challenge) wait in the
/// server-side session between the two calls and are removed when the second call reads them, so a challenge
/// answers once.
/// </summary>
[ApiController]
[Route("api/patient/passkey")]
public sealed class PatientPasskeyController(
    CallerManager callerManager,
    EnrollmentManager enrollmentManager
) : ControllerBase
{
    private const string RegistrationSessionKey = "passkey.registration";
    private const string SignInSessionKey = "passkey.sign-in";
    private const string HomePage = "/Patient/Home";

    [HttpPost("create-registration-options")]
    public async Task<IActionResult> CreateRegistrationOptionsAsync(
        CreateRegistrationOptionsRequest request
    )
    {
        CreateRegistrationOptionsResult result =
            await enrollmentManager.CreateRegistrationOptionsAsync(
                request.InviteCode,
                request.DisplayName
            );
        if (!result.Succeeded)
        {
            return BadRequest(new ErrorResponse(Copy.EnrollmentFailed(result.Error!.Value)));
        }
        string optionsJson = result.Options!.ToJson();
        HttpContext.Session.SetString(
            RegistrationSessionKey,
            JsonSerializer.Serialize(new PendingRegistration(request.InviteCode, optionsJson))
        );
        return Content(optionsJson, "application/json");
    }

    [HttpPost("complete-registration")]
    public async Task<IActionResult> CompleteRegistrationAsync(CompleteRegistrationRequest request)
    {
        string? pendingJson = HttpContext.Session.GetString(RegistrationSessionKey);
        HttpContext.Session.Remove(RegistrationSessionKey);
        if (pendingJson is null)
        {
            return BadRequest(new ErrorResponse(Copy.PasskeyFailed));
        }
        PendingRegistration pending = JsonSerializer.Deserialize<PendingRegistration>(pendingJson)!;
        EnrollmentResult result = await enrollmentManager.CompleteRegistrationAsync(
            pending.InviteCode,
            request.AppUris ?? [],
            request.Attestation,
            CredentialCreateOptions.FromJson(pending.OptionsJson)
        );
        return await SignInResultAsync(result);
    }

    [HttpPost("create-sign-in-options")]
    public IActionResult CreateSignInOptions()
    {
        string optionsJson = enrollmentManager.CreateSignInOptions().ToJson();
        HttpContext.Session.SetString(SignInSessionKey, optionsJson);
        return Content(optionsJson, "application/json");
    }

    [HttpPost("complete-sign-in")]
    public async Task<IActionResult> CompleteSignInAsync(CompleteSignInRequest request)
    {
        string? optionsJson = HttpContext.Session.GetString(SignInSessionKey);
        HttpContext.Session.Remove(SignInSessionKey);
        if (optionsJson is null)
        {
            return BadRequest(new ErrorResponse(Copy.PasskeyFailed));
        }
        EnrollmentResult result = await enrollmentManager.CompleteSignInAsync(
            request.Assertion,
            AssertionOptions.FromJson(optionsJson)
        );
        return await SignInResultAsync(result);
    }

    private async Task<IActionResult> SignInResultAsync(EnrollmentResult result)
    {
        if (!result.Succeeded)
        {
            return BadRequest(new ErrorResponse(Copy.EnrollmentFailed(result.Error!.Value)));
        }
        await HttpContext.SignInAsync(
            CallerManager.PatientScheme,
            callerManager.PatientPrincipal(result.PatientId!.Value)
        );
        return Ok(new PasskeyCompletedResponse(HomePage));
    }

    private sealed record PendingRegistration(string InviteCode, string OptionsJson);
}
