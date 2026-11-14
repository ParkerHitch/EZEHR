using System.Text.Json;
using EZEHR.Access;
using EZEHR.Auth;
using EZEHR.Domain;
using EZEHR.Records;
using EZEHR.Security.Pki;
using Microsoft.Extensions.Options;

namespace EZEHR.Api;

public sealed record InviteRequest(string ProviderPatientId);
public sealed record IngestRequest(string ProviderPatientId, string[] Entries);
public sealed record NoteRequest(string Jws);

/// <summary>
/// The network door: organizations and apps call these endpoints over mutual TLS with EZCA certificates.
/// 401 means the certificate itself was not accepted; 403 means a verified caller isn't allowed this action.
/// </summary>
public static class NetworkApi
{
    public static void MapNetworkApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/network");

        api.MapPost("/invites", async (InviteRequest req, HttpContext ctx, IClientCertificateSource certs, CallerResolver resolver,
            InviteService invites, IOptions<AppOptions> options) =>
        {
            var caller = resolver.Resolve(await certs.GetAsync(ctx));
            if (Reject(caller, PartyKind.Provider) is { } rejected) return rejected;
            if (string.IsNullOrWhiteSpace(req.ProviderPatientId)) return Error(400, "providerPatientId_required");

            var code = invites.Create(((VerifiedCaller)caller).Party.SubjectUri, req.ProviderPatientId);
            var url = $"{options.Value.Origin.TrimEnd('/')}/Patient/Enroll?invite={code}";
            return Results.Json(new { code, url }, statusCode: StatusCodes.Status201Created);
        });

        api.MapPost("/records", async (IngestRequest req, HttpContext ctx, IClientCertificateSource certs, CallerResolver resolver,
            InviteService invites, RecordVerifier verifier, RecordStore store, AuditLog audit) =>
        {
            var caller = resolver.Resolve(await certs.GetAsync(ctx));
            if (Reject(caller, PartyKind.Provider) is { } rejected) return rejected;
            var provider = ((VerifiedCaller)caller).Party;

            if (invites.LinkedPatient(provider.SubjectUri, req.ProviderPatientId ?? "") is not { } patientId)
                return Error(409, "patient_not_linked");

            // Verify everything before storing anything, so a bad batch stores nothing.
            var verified = new List<(string SourceKey, VerifiedEntry Entry, string Jws)>();
            foreach (var jws in req.Entries ?? [])
            {
                VerifiedEntry entry;
                try { entry = verifier.Verify(Guid.Empty, jws); }
                catch (RecordSignatureException) { return Error(400, "bad_signature"); }
                if (entry.Signer.SubjectUri != provider.SubjectUri) return Error(400, "signer_mismatch");
                if (!entry.Resource.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } resourceId)
                    return Error(400, "resource_id_required");
                verified.Add(($"{entry.Signer.SubjectUri}|{entry.ResourceType}/{resourceId}", entry, jws));
            }

            int inserted = 0, skipped = 0;
            foreach (var (sourceKey, entry, jws) in verified)
            {
                var (added, _) = await store.PutAsync(patientId, sourceKey, entry.ResourceType, jws);
                if (added) inserted++; else skipped++;
            }
            audit.Append(patientId, provider, "Ingest", null, true, null);
            return Results.Ok(new { inserted, skipped });
        });

        // Patient-targeted endpoints: every call goes through the gate, which logs it for the patient.
        api.MapGet("/patients/{patientId:guid}/labs", async (Guid patientId, HttpContext ctx, IClientCertificateSource certs,
            CallerResolver resolver, RecordReader reader) =>
        {
            var caller = resolver.Resolve(await certs.GetAsync(ctx));
            var (decision, entries) = await reader.ReadAsync(patientId, AccessAction.ReadLabs, caller, null);
            return decision.Allowed ? Results.Ok(new { entries = entries.Select(View) }) : Denied(decision);
        });

        api.MapGet("/patients/{patientId:guid}/record", async (Guid patientId, string? purpose, HttpContext ctx,
            IClientCertificateSource certs, CallerResolver resolver, RecordReader reader) =>
        {
            var caller = resolver.Resolve(await certs.GetAsync(ctx));
            var (decision, entries) = await reader.ReadAsync(patientId, AccessAction.ReadRecord, caller, purpose);
            return decision.Allowed ? Results.Ok(new { entries = entries.Select(View) }) : Denied(decision);
        });

        api.MapPost("/patients/{patientId:guid}/notes", async (Guid patientId, NoteRequest req, HttpContext ctx,
            IClientCertificateSource certs, CallerResolver resolver, AccessGate gate, RecordVerifier verifier, RecordStore store) =>
        {
            var caller = resolver.Resolve(await certs.GetAsync(ctx));
            var decision = gate.Authorize(patientId, AccessAction.WriteNote, caller, null);
            if (!decision.Allowed) return Denied(decision);

            VerifiedEntry note;
            try { note = verifier.Verify(Guid.Empty, req.Jws ?? ""); }
            catch (RecordSignatureException) { return Error(400, "bad_signature"); }
            if (note.Signer.SubjectUri != ((VerifiedCaller)caller).Party.SubjectUri) return Error(400, "signer_mismatch");
            if (note.ResourceType != "Communication") return Error(400, "notes_must_be_communication");
            if (!note.Resource.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } noteId) return Error(400, "resource_id_required");

            var (inserted, entryId) = await store.PutAsync(patientId, $"{note.Signer.SubjectUri}|Communication/{noteId}", "Communication", req.Jws!);
            return Results.Ok(new { inserted, id = entryId });
        });
    }

    /// <summary>401 for certificate problems, 403 for a verified caller of the wrong kind; null when the caller may proceed.</summary>
    internal static IResult? Reject(Caller caller, PartyKind required) => caller switch
    {
        UnverifiedCaller u => Error(401, u.Reason.ToString()),
        VerifiedCaller v when v.Party.Kind != required => Error(403, "wrong_party_kind"),
        _ => null,
    };

    private static IResult Denied(AccessDecision d) =>
        Error(AccessGate.IsAuthenticationFailure(d.Reason!.Value) ? 401 : 403, d.Reason.Value.ToString());

    private static object View(SignedEntry e) => new
    {
        id = e.Verified.Id,
        resourceType = e.Verified.ResourceType,
        signer = e.Verified.Signer,
        signedAt = e.Verified.SignedAt,
        writtenBy = e.Verified.WrittenBy,
        resource = e.Verified.Resource,
        jws = e.Jws,
    };

    internal static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
}
