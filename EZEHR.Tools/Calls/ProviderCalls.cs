using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;
using QRCoder;
using Spectre.Console;

namespace EZEHR.Tools.Calls;

/// <summary>The organization audit's filters; each one left null doesn't filter.</summary>
public sealed record OrganizationAuditQuery(
    string? ClinicianUri,
    Guid? PatientId,
    string? Chart,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    int Limit
);

/// <summary>
/// A provider's EHR system on the network, with its certificate: invites a patient, pushes signed records, opens
/// EZEHR for a clinician, and reads its own audit trail. Each returns whether the server accepted the call.
/// </summary>
public static class ProviderCalls
{
    public const int DefaultAuditLimit = 100;

    /// <summary>Prints the invite link with a QR code of it, for the patient's phone to scan.</summary>
    public static async Task<bool> InviteAsync(HttpClient client, string chart)
    {
        const string path = "api/network/create-enrollment-invite";
        NetworkLog.Request("POST", path, $"for its patient {chart}");
        using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
            client,
            path,
            new CreateEnrollmentInviteRequest(chart)
        );
        if (response.StatusCode != HttpStatusCode.Created)
        {
            await NetworkLog.FailedAsync(response);
            return false;
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
        IAnsiConsole console = AnsiConsole.Console;
        console.WriteLine();
        console.WriteLine($"  {invite.EnrollmentUrl}");
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(
            invite.EnrollmentUrl,
            QRCodeGenerator.ECCLevel.L
        );
        console.WriteLine(new AsciiQRCode(data).GetGraphicSmall());
        return true;
    }

    /// <summary>
    /// One request per visit date, so each stays far below the 100 KB renegotiation limit. Every resource is signed
    /// now: the server checks the signer's chain at the JWS iat, and a certificate issued today isn't valid on a
    /// 2024 visit date. The clinical date stays in the resource itself (effectiveDateTime, period, ...).
    /// </summary>
    public static async Task<bool> PushAsync(
        HttpClient client,
        PfxFile signer,
        string chart,
        IReadOnlyList<ResourceVisit> visits,
        bool corruptSignature
    )
    {
        const string path = "api/network/push-signed-records";
        foreach (ResourceVisit visit in visits)
        {
            DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
            string[] signedEntries = visit
                .Resources.Select(resource =>
                {
                    string jws = RecordSigner.Sign(
                        resource,
                        signer.Certificate,
                        signer.Chain,
                        issuedAt
                    );
                    return corruptSignature ? RecordTampering.CorruptSignature(jws) : jws;
                })
                .ToArray();
            string signedBy = signer.Certificate.GetNameInfo(
                X509NameType.SimpleName,
                forIssuer: false
            );
            NetworkLog.Request(
                "POST",
                path,
                $"{chart} · visit {visit.Date:yyyy-MM-dd} · {signedEntries.Length} records, each signed by {signedBy}{(corruptSignature ? ", signatures corrupted" : "")}"
            );
            using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
                client,
                path,
                new PushSignedRecordsRequest(chart, signedEntries)
            );
            if (!response.IsSuccessStatusCode)
            {
                await NetworkLog.FailedAsync(response);
                return false;
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
        return true;
    }

    /// <summary>
    /// What the provider's own system does when a clinician clicks "open EZEHR" in a patient's chart: asks EZEHR for
    /// a launch for that clinician, chart and visit, and prints the link the clinician's browser would be sent to.
    /// </summary>
    public static async Task<bool> LaunchAsync(
        HttpClient client,
        string chart,
        Guid clinicianObjectId,
        string encounterId
    )
    {
        const string path = "api/network/create-clinician-launch";
        NetworkLog.Request(
            "POST",
            path,
            $"for its clinician {clinicianObjectId}, chart {chart}, visit {encounterId}"
        );
        using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
            client,
            path,
            new CreateClinicianLaunchRequest(clinicianObjectId, chart, encounterId)
        );
        if (response.StatusCode != HttpStatusCode.Created)
        {
            await NetworkLog.FailedAsync(response);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                AnsiConsole.Console.WriteLine(
                    "  This provider has no Entra tenant in EZEHR's organizations table, so it has no clinicians to launch."
                );
            }
            return false;
        }
        CreateClinicianLaunchResponse launch = (
            await response.Content.ReadFromJsonAsync<CreateClinicianLaunchResponse>(
                NetworkClient.Json
            )
        )!;
        NetworkLog.Succeeded(
            response,
            $"a launch link for that clinician only, to open within {LaunchManager.LaunchOpenWindow.TotalMinutes:0} minutes"
        );
        AnsiConsole.Console.WriteLine();
        AnsiConsole.Console.WriteLine($"  {launch.LaunchUrl}");
        return true;
    }

    /// <summary>Every page of the provider's audit, one line per attempt, oldest first.</summary>
    public static async Task<bool> ReadAuditAsync(HttpClient client, OrganizationAuditQuery query)
    {
        long afterId = 0;
        while (true)
        {
            string path = $"api/network/read-organization-audit?{AuditQuery(query, afterId)}";
            NetworkLog.Request("GET", path, "");
            using HttpResponseMessage response = await client.GetAsync(path);
            if (!response.IsSuccessStatusCode)
            {
                await NetworkLog.FailedAsync(response);
                return false;
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
                string visit = auditEvent.EncounterId is null
                    ? ""
                    : $"  visit {auditEvent.EncounterId}";
                // Emergency access was opened without the patient's approval, so the reason given is what to review.
                string emergencyReason =
                    auditEvent.Action == GrantManager.OpenEmergencyAccessAction
                        ? $"  reason: {auditEvent.ClaimedPurpose}"
                        : "";
                AnsiConsole.Console.WriteLine(
                    $"    {auditEvent.At:yyyy-MM-dd HH:mm}  {auditEvent.ActorName, -20}  {auditEvent.Action, -19}  {outcome}  patient {patient}{visit}{emergencyReason}"
                );
            }
            if (page.NextAfterId is not long nextAfterId)
            {
                return true;
            }
            afterId = nextAfterId;
        }
    }

    /// <summary>The audit's query string for one page: afterId and limit first, then each filter that is set.</summary>
    public static string AuditQuery(OrganizationAuditQuery query, long afterId)
    {
        StringBuilder builder = new($"afterId={afterId}&limit={query.Limit}");
        Append(builder, "clinicianUri", query.ClinicianUri);
        Append(builder, "patientId", query.PatientId?.ToString());
        Append(builder, "providerPatientId", query.Chart);
        Append(builder, "since", query.Since?.ToString("O"));
        Append(builder, "until", query.Until?.ToString("O"));
        return builder.ToString();
    }

    /// <summary>The clinicians who have acted for the provider, most recently seen first.</summary>
    public static async Task<bool> ListCliniciansAsync(HttpClient client)
    {
        const string path = "api/network/list-organization-clinicians";
        NetworkLog.Request("GET", path, "");
        using HttpResponseMessage response = await client.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            await NetworkLog.FailedAsync(response);
            return false;
        }
        ListOrganizationCliniciansResponse list = (
            await response.Content.ReadFromJsonAsync<ListOrganizationCliniciansResponse>(
                NetworkClient.Json
            )
        )!;
        NetworkLog.Succeeded(
            response,
            list.Clinicians.Count == 1 ? "1 clinician" : $"{list.Clinicians.Count} clinicians"
        );
        Table table = new Table()
            .AddColumn("clinician")
            .AddColumn("events")
            .AddColumn("refused")
            .AddColumn("last seen")
            .AddColumn("clinician URI");
        foreach (OrganizationClinician clinician in list.Clinicians)
        {
            table.AddRow(
                new Text(clinician.DisplayName),
                new Text(clinician.EventCount.ToString()),
                new Text(clinician.DeniedCount.ToString()),
                new Text($"{clinician.LastSeenAt:yyyy-MM-dd HH:mm}"),
                new Text(clinician.ClinicianUri)
            );
        }
        AnsiConsole.Console.Write(table);
        return true;
    }

    private static void Append(StringBuilder builder, string name, string? value)
    {
        if (value is not null)
        {
            builder.Append($"&{name}={Uri.EscapeDataString(value)}");
        }
    }
}
