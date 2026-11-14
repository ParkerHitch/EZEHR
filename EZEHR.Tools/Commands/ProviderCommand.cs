using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EZEHR.Managers;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Net;

namespace EZEHR.Tools.Commands;

/// <summary>
/// Stands in for a provider's EHR on the network: asks for an enrollment invite for one of its patients, and
/// pushes that patient's records from seed/{provider}.json, signed with .pki/{provider}.pfx.
/// </summary>
public static class ProviderCommand
{
    public const string Usage = """
          provider invite <lakeside|northwind|maple|riverside> <providerPatientId>
          provider push <lakeside|northwind|maple|riverside> <providerPatientId>
        """;

    public static async Task<int> RunAsync(string[] args)
    {
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

    private static async Task<int> InviteAsync(HttpClient client, string providerPatientId)
    {
        using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
            client,
            "api/network/create-enrollment-invite",
            new CreateEnrollmentInviteRequest(providerPatientId)
        );
        if (response.StatusCode != HttpStatusCode.Created)
        {
            Console.Error.WriteLine(await NetworkClient.DescribeAsync(response));
            return 1;
        }
        CreateEnrollmentInviteResponse invite = (
            await response.Content.ReadFromJsonAsync<CreateEnrollmentInviteResponse>(
                NetworkClient.Json
            )
        )!;
        Console.WriteLine($"invite code: {GrantManager.FormatCode(invite.InviteCode)}");
        Console.WriteLine(invite.EnrollmentUrl);
        return 0;
    }

    /// <summary>
    /// One request per visit date, so each stays far below the 100 KB renegotiation limit. Every resource is signed
    /// now: the server checks the signer's chain at the JWS iat, and a certificate issued today isn't valid on a
    /// 2024 visit date. The clinical date stays in the resource itself (effectiveDateTime, period, ...).
    /// </summary>
    private static async Task<int> PushAsync(
        HttpClient client,
        string slug,
        string providerPatientId
    )
    {
        PfxFile signingCertificate = PfxFile.Load(ToolPaths.Pki($"{slug}.pfx"));
        SeedFile seed = SeedFile.Load(slug);
        int stored = 0;
        int alreadyStored = 0;
        int replaced = 0;
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
            using HttpResponseMessage response = await NetworkClient.PostJsonAsync(
                client,
                "api/network/push-signed-records",
                new PushSignedRecordsRequest(providerPatientId, signedEntries)
            );
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(
                    $"{visit.Date:yyyy-MM-dd}: {await NetworkClient.DescribeAsync(response)}"
                );
                return 1;
            }
            PushSignedRecordsResponse result = (
                await response.Content.ReadFromJsonAsync<PushSignedRecordsResponse>(
                    NetworkClient.Json
                )
            )!;
            stored += result.Stored;
            alreadyStored += result.AlreadyStored;
            replaced += result.Replaced;
            Console.WriteLine(
                $"{visit.Date:yyyy-MM-dd}: stored {result.Stored}, already stored {result.AlreadyStored}, replaced {result.Replaced}"
            );
        }
        Console.WriteLine(
            $"{seed.Organization}: stored {stored}, already stored {alreadyStored}, replaced {replaced}"
        );
        return 0;
    }

    private static bool IsProvider(string slug)
    {
        return OrgDirectory.KnownOrganizations.TryGetValue(
                OrgDirectory.ProviderCommonName(slug),
                out Party? party
            )
            && party.Kind == PartyKind.Provider;
    }

    private sealed record SeedFile(string Provider, string Organization, SeedVisit[] Visits)
    {
        public static SeedFile Load(string slug)
        {
            string path = Path.Combine(ToolPaths.SeedDirectory, $"{slug}.json");
            return JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(path), NetworkClient.Json)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
    }

    private sealed record SeedVisit(DateOnly Date, JsonElement[] Resources);
}
