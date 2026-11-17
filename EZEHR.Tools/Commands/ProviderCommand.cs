using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;
using QRCoder;

namespace EZEHR.Tools.Commands;

/// <summary>
/// Stands in for a provider's EHR on the network: asks for an enrollment invite for one of its patients, pushes
/// that patient's records from seed/{provider}.json, signed with .pki/{provider}.pfx, and reads the provider's own
/// audit trail (what its clinicians and its system did). Works for the demo providers and any onboarded one; only
/// the demo providers have seed records to push.
/// </summary>
public static class ProviderCommand
{
    public const string Usage = """
          provider invite <provider> <providerPatientId>
          provider push <provider> <providerPatientId>
          provider audit <provider> [--clinician <uri>] [--patient <providerPatientId>]
          provider clinicians <provider>
            <provider>: lakeside, northwind, maple, riverside, or one onboarded with `certs issue --provider`
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length >= 2 && args[0] is "audit" or "clinicians" && IsProvider(args[1]))
        {
            using HttpClient auditClient = NetworkClient.Create(
                NetworkClient.BaseUrl,
                ToolPaths.Pki($"{args[1]}.pfx")
            );
            if (args[0] == "audit")
            {
                return await AuditAsync(
                    auditClient,
                    CommandLine.Option(args, "--clinician"),
                    CommandLine.Option(args, "--patient")
                );
            }
            return await CliniciansAsync(auditClient);
        }
        if (args.Length != 3 || !IsProvider(args[1]))
        {
            Console.Error.WriteLine($"usage:\n{Usage}");
            return 1;
        }
        string slug = args[1];
        string providerPatientId = args[2];
        using HttpClient client = NetworkClient.Create(
            NetworkClient.BaseUrl,
            ToolPaths.Pki($"{slug}.pfx")
        );
        if (args[0] == "invite")
        {
            return await InviteAsync(client, providerPatientId);
        }
        if (args[0] == "push")
        {
            return await PushAsync(client, slug, providerPatientId);
        }
        Console.Error.WriteLine($"usage:\n{Usage}");
        return 1;
    }

    /// <summary>Prints the invite link with a QR code of it, for the patient's phone to scan.</summary>
    public static async Task<int> InviteAsync(HttpClient client, string providerPatientId)
    {
        const string path = "api/network/create-enrollment-invite";
        NetworkLog.Request("POST", path, $"for its patient {providerPatientId}");
        using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
            client,
            path,
            new CreateEnrollmentInviteRequest(providerPatientId)
        );
        if (response.StatusCode != HttpStatusCode.Created)
        {
            await NetworkLog.FailedAsync(response);
            return 1;
        }
        CreateEnrollmentInviteResponse invite = (
            await response.Content.ReadFromJsonAsync<CreateEnrollmentInviteResponse>(
                NetworkClient.Json
            )
        )!;
        NetworkLog.Succeeded(
            response,
            $"invite code {GrantManager.FormatCode(invite.InviteCode)}, single use, valid 7 days"
        );
        Console.WriteLine();
        Console.WriteLine($"  {invite.EnrollmentUrl}");
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(
            invite.EnrollmentUrl,
            QRCodeGenerator.ECCLevel.L
        );
        Console.WriteLine(new AsciiQRCode(data).GetGraphicSmall());
        return 0;
    }

    /// <summary>
    /// One request per visit date, so each stays far below the 100 KB renegotiation limit. Every resource is signed
    /// now: the server checks the signer's chain at the JWS iat, and a certificate issued today isn't valid on a
    /// 2024 visit date. The clinical date stays in the resource itself (effectiveDateTime, period, ...).
    /// </summary>
    public static async Task<int> PushAsync(
        HttpClient client,
        string slug,
        string providerPatientId
    )
    {
        const string path = "api/network/push-signed-records";
        PfxFile signingCertificate = PfxFile.Load(ToolPaths.Pki($"{slug}.pfx"));
        if (!File.Exists(SeedFile.PathFor(slug)))
        {
            Console.Error.WriteLine(
                $"{slug} has no seed records to push; only the demo providers do."
            );
            return 1;
        }
        SeedFile seed = SeedFile.Load(slug);
        foreach (SeedVisit visit in seed.Visits)
        {
            DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
            string[] signedEntries = visit
                .Resources.Select(resource =>
                    RecordSigner.Sign(
                        resource,
                        signingCertificate.Certificate,
                        signingCertificate.Chain,
                        issuedAt
                    )
                )
                .ToArray();
            NetworkLog.Request(
                "POST",
                path,
                $"{providerPatientId} · visit {visit.Date:yyyy-MM-dd} · {signedEntries.Length} records, each signed"
            );
            using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
                client,
                path,
                new PushSignedRecordsRequest(providerPatientId, signedEntries)
            );
            if (!response.IsSuccessStatusCode)
            {
                await NetworkLog.FailedAsync(response);
                return 1;
            }
            PushSignedRecordsResponse result = (
                await response.Content.ReadFromJsonAsync<PushSignedRecordsResponse>(
                    NetworkClient.Json
                )
            )!;
            NetworkLog.Succeeded(
                response,
                $"stored {result.Stored}, already stored {result.AlreadyStored}, replaced {result.Replaced}"
            );
        }
        return 0;
    }

    /// <summary>Every page of the provider's audit, one line per attempt, oldest first.</summary>
    public static async Task<int> AuditAsync(
        HttpClient client,
        string? clinicianUri,
        string? providerPatientId
    )
    {
        string filters = "";
        if (clinicianUri is not null)
        {
            filters += $"&clinicianUri={Uri.EscapeDataString(clinicianUri)}";
        }
        if (providerPatientId is not null)
        {
            filters += $"&providerPatientId={Uri.EscapeDataString(providerPatientId)}";
        }
        long afterId = 0;
        while (true)
        {
            string path = $"api/network/read-organization-audit?afterId={afterId}{filters}";
            NetworkLog.Request("GET", path, "");
            using HttpResponseMessage response = await client.GetAsync(path);
            if (!response.IsSuccessStatusCode)
            {
                await NetworkLog.FailedAsync(response);
                return 1;
            }
            ReadOrganizationAuditResponse page = (
                await response.Content.ReadFromJsonAsync<ReadOrganizationAuditResponse>(
                    NetworkClient.Json
                )
            )!;
            NetworkLog.Succeeded(
                response,
                page.Events.Count == 1 ? "1 event" : $"{page.Events.Count} events"
            );
            foreach (OrganizationAuditEvent auditEvent in page.Events)
            {
                string outcome = auditEvent.Granted ? "allowed" : $"refused {auditEvent.Reason}";
                string patient = auditEvent.ProviderPatientId ?? auditEvent.PatientId.ToString();
                // Emergency access was opened without the patient's approval, so the reason given is what to review.
                string emergencyReason =
                    auditEvent.Action == GrantManager.OpenEmergencyAccessAction
                        ? $"  reason: {auditEvent.ClaimedPurpose}"
                        : "";
                Console.WriteLine(
                    $"    {auditEvent.At:yyyy-MM-dd HH:mm}  {auditEvent.ActorName, -20}  {auditEvent.Action, -19}  {outcome}  patient {patient}{emergencyReason}"
                );
            }
            if (page.NextAfterId is not long nextAfterId)
            {
                return 0;
            }
            afterId = nextAfterId;
        }
    }

    private static async Task<int> CliniciansAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "api/network/list-organization-clinicians"
        );
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(await NetworkClient.DescribeAsync(response));
            return 1;
        }
        ListOrganizationCliniciansResponse list = (
            await response.Content.ReadFromJsonAsync<ListOrganizationCliniciansResponse>(
                NetworkClient.Json
            )
        )!;
        foreach (OrganizationClinician clinician in list.Clinicians)
        {
            Console.WriteLine(
                $"{clinician.DisplayName, -20}  {clinician.EventCount} events ({clinician.DeniedCount} refused)  last {clinician.LastSeenAt:yyyy-MM-dd HH:mm}  {clinician.ClinicianUri}"
            );
        }
        return 0;
    }

    private static bool IsProvider(string slug)
    {
        return ToolOrganizations.Load().FindByUri(OrgDirectory.ProviderUri(slug))?.Kind
            == PartyKind.Provider;
    }

    private sealed record SeedFile(string Provider, string Organization, SeedVisit[] Visits)
    {
        public static string PathFor(string slug)
        {
            return Path.Combine(ToolPaths.SeedDirectory, $"{slug}.json");
        }

        public static SeedFile Load(string slug)
        {
            string path = PathFor(slug);
            return JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(path), NetworkClient.Json)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
    }

    private sealed record SeedVisit(DateOnly Date, JsonElement[] Resources);
}
