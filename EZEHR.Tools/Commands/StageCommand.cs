using System.Security.Cryptography.X509Certificates;
using EZEHR.Models;
using EZEHR.Services;
using EZEHR.Tools.Calls;
using EZEHR.Tools.Cli;
using EZEHR.Tools.Net;
using Spectre.Console;

namespace EZEHR.Tools.Commands;

/// <summary>
/// The demo's network calls, in order, one per key press. Each step names the system making the call and the
/// certificate it presents, then shows every request and response. Everything on screen is a real call, to the CA
/// (the local test CA by default, EZCA with --ca ezca) or to /api/network; the only thing done behind the scenes is
/// finding Sam's EZEHR patient id in the database.
/// </summary>
public static class StageCommand
{
    public const string Usage = """
          stage [--from <step>] [--ca local|ezca] [--ca-id <id>] [--template-id <id>]
                                  the demo's network calls in order, one per key press
          stage --list            the steps, numbered
        """;

    /// <summary>The provider that joins the network on camera. `demo reset` takes it off first.</summary>
    public const string JoiningProviderSlug = "northwind";

    /// <summary>The app that joins the network on camera, after Kidney Check. `demo reset` takes it off first.</summary>
    public const string JoiningAppSlug = "medication-check";

    private const int BannerWidth = 76;

    /// <summary>Certificate is read when the banner prints, since step 1 issues one.</summary>
    private sealed record Actor(
        string Name,
        string Role,
        Func<string> Certificate,
        ConsoleColor Color
    );

    /// <summary>Dr. Rivera's object id in Riverside's Entra tenant, which Riverside's system names in its launch.</summary>
    public const string ClinicianObjectIdVariable = "EZEHR_CLINICIAN_OBJECT_ID";

    private sealed record Step(Actor Actor, string Action, Func<Task<bool>> RunAsync);

    public static async Task<int> RunAsync(string[] args)
    {
        if (
            string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(AppOptions.ConnectionStringKey)
            )
        )
        {
            Console.Error.WriteLine(
                $"{AppOptions.ConnectionStringKey} isn't set (it's how the stage finds Sam's patient id). For the deployed site: source scripts/azure.env"
            );
            return 1;
        }
        string authority = CommandLine.Option(args, "--ca") ?? "local";
        if (authority is not ("local" or "ezca"))
        {
            Console.Error.WriteLine("--ca must be local or ezca");
            return 1;
        }
        List<Step> steps = BuildSteps(
            authority,
            CommandLine.Option(args, "--ca-id"),
            CommandLine.Option(args, "--template-id")
        );

        if (args.Contains("--list"))
        {
            for (int index = 0; index < steps.Count; index++)
            {
                Console.WriteLine(
                    $"{index + 1, 3}. {steps[index].Actor.Name}: {steps[index].Action}"
                );
            }
            return 0;
        }
        string? fromOption = CommandLine.Option(args, "--from");
        int firstStep = 1;
        if (
            fromOption is not null
            && (
                !int.TryParse(fromOption, out firstStep) || firstStep < 1 || firstStep > steps.Count
            )
        )
        {
            Console.Error.WriteLine($"--from takes a step number from 1 to {steps.Count}.");
            return 1;
        }

        Console.WriteLine($"EZEHR network: {NetworkClient.BaseUrl}");
        for (int index = firstStep - 1; index < steps.Count; index++)
        {
            Step step = steps[index];
            PrintBanner(index + 1, steps.Count, step);
            char start = WaitForKey("Enter: send   s: skip   q: quit", "\rsq");
            if (start == 'q')
            {
                return 0;
            }
            if (start == 's')
            {
                continue;
            }
            while (!await RunStepAsync(step))
            {
                char choice = WaitForKey("r: retry   s: skip   q: quit", "rsq");
                if (choice == 'q')
                {
                    return 1;
                }
                if (choice == 's')
                {
                    break;
                }
                Console.WriteLine();
            }
        }
        Console.WriteLine();
        Console.WriteLine("That was every step.");
        return 0;
    }

    private static List<Step> BuildSteps(string authority, string? caId, string? templateId)
    {
        Actor northwind = Provider("northwind", ConsoleColor.Blue);
        Actor lakeside = Provider("lakeside", ConsoleColor.Cyan);
        Actor maple = Provider("maple", ConsoleColor.DarkYellow);
        Actor riverside = Provider("riverside", ConsoleColor.Magenta);
        Actor northwindJoining = northwind with
        {
            Role = "its IT team, joining the network",
            Certificate = () => "none yet: it asks the CA for one",
        };
        Actor platform = new(
            "EZEHR",
            "the network's operator",
            () => DescribeCertificate("platform.pfx"),
            ConsoleColor.Green
        );
        Actor kidneyCheck = new(
            KidneyCheckCommand.AppName,
            "an app Sam allowed to read lab results",
            () => DescribeCertificate("kidney-check.pfx"),
            ConsoleColor.Yellow
        );
        Actor kidneyCheckOverreaching = kidneyCheck with
        {
            Role = "the same app, asking for more than Sam allowed",
        };
        Actor medicationCheck = new(
            MedicationCheckCommand.AppName,
            "a new app, allowed to read medications as well as labs once Sam turns it on",
            () => DescribeCertificate("medication-check.pfx"),
            ConsoleColor.DarkGreen
        );
        Organization joiningApp = OrgDirectory.DemoOrganizations.Single(organization =>
            organization.Uri == OrgDirectory.AppUri(JoiningAppSlug)
        );
        Actor brokerWithoutCertificate = new(
            "Data broker",
            "a system nobody verified",
            () => "none",
            ConsoleColor.Red
        );
        Actor brokerWithOwnCertificate = brokerWithoutCertificate with
        {
            Certificate = () =>
                $"{CertificateIssuance.BrokerCommonName}, self-signed: it made the certificate itself, the network's CA didn't issue it",
        };
        string northwindName = northwind.Name;

        Guid? samId = null;
        KidneyResult? kidneyResult = null;

        async Task<Guid?> FindSamAsync()
        {
            samId ??= await PatientReference.Sam.ResolveAsync();
            return samId;
        }

        Step Invite(Actor actor, string slug, string providerPatientId)
        {
            return new Step(
                actor,
                $"asks EZEHR for an enrollment invite for its patient {providerPatientId} (Sam)",
                async () =>
                {
                    using HttpClient client = ProviderClient(slug);
                    return await ProviderCalls.InviteAsync(client, providerPatientId);
                }
            );
        }

        Step Push(Actor actor, string slug, string providerPatientId, string action)
        {
            return new Step(actor, action, async () =>
            {
                using HttpClient client = ProviderClient(slug);
                return await ProviderCalls.PushAsync(
                    client,
                    PfxFile.Load(ToolPaths.Pki($"{slug}.pfx")),
                    providerPatientId,
                    ResourceFile.LoadSeed(slug),
                    corruptSignature: false
                );
            });
        }

        Step Broker(Actor actor, string action, bool withSelfSignedCertificate)
        {
            return new Step(actor, action, async () =>
            {
                if (await FindSamAsync() is not Guid patientId)
                {
                    return false;
                }
                await BrokerCommand.RequestAsync(patientId, withSelfSignedCertificate);
                return true;
            });
        }

        const string pushSamsRecords = "pushes Sam's records, each one signed with its own key";
        return
        [
            // Northwind joins the network.
            new Step(
                northwindJoining,
                authority == "ezca"
                    ? "gets its certificate from EZCA (portal.ezca.io)"
                    : "gets its certificate from the local test CA (stands in for EZCA)",
                async () =>
                {
                    await CertificateIssuance.IssueOrganizationAsync(
                        new CertificateAuthoritySettings
                        {
                            Authority = authority,
                            CaId = caId,
                            TemplateId = templateId,
                        },
                        OrgDirectory.ProviderCommonName(JoiningProviderSlug)
                    );
                    return true;
                }
            ),
            new Step(
                platform,
                $"adds {northwindName} to the network: its name and its certificate's CN",
                async () =>
                {
                    await PlatformCalls.RegisterProviderAsync(
                        NetworkClient.BaseUrl,
                        JoiningProviderSlug,
                        northwindName
                    );
                    return true;
                }
            ),
            // Sam's providers invite Sam. Sam enrolls with Northwind's, then adds the others.
            Invite(northwind, "northwind", "NWH-88213"),
            Invite(lakeside, "lakeside", "LUC-20417"),
            Invite(maple, "maple", "MSF-0932"),
            Push(northwind, "northwind", "NWH-88213", pushSamsRecords),
            Push(lakeside, "lakeside", "LUC-20417", pushSamsRecords),
            Push(maple, "maple", "MSF-0932", pushSamsRecords),
            new Step(
                kidneyCheck,
                "reads Sam's kidney labs and checks who signed each one",
                async () =>
                {
                    if (await FindSamAsync() is not Guid patientId)
                    {
                        return false;
                    }
                    using HttpClient client = AppClient();
                    kidneyResult = await KidneyCheckCommand.ReadEgfrLabsAsync(client, patientId);
                    return kidneyResult is not null;
                }
            ),
            new Step(
                kidneyCheck,
                "writes what it found back to Sam's record, as a signed note",
                async () =>
                {
                    if (await FindSamAsync() is not Guid patientId)
                    {
                        return false;
                    }
                    if (kidneyResult is null)
                    {
                        Console.Error.WriteLine(
                            "  Kidney Check hasn't read the labs in this run: skip this, or quit and start again from its read step (stage --list)."
                        );
                        return false;
                    }
                    using HttpClient client = AppClient();
                    return await KidneyCheckCommand.WriteSignedNoteAsync(
                            client,
                            PfxFile.Load(ToolPaths.Pki("kidney-check.pfx")),
                            patientId,
                            kidneyResult
                        ) == 0;
                }
            ),
            new Step(
                kidneyCheckOverreaching,
                "asks for Sam's medications, which its grant doesn't cover",
                async () =>
                {
                    if (await FindSamAsync() is not Guid patientId)
                    {
                        return false;
                    }
                    using HttpClient client = AppClient();
                    return await AskForMedicationsOutsideScopeAsync(client, patientId);
                }
            ),
            // A second app joins the platform; Sam turns it on from Access → Apps before the next step.
            new Step(
                platform,
                $"adds {joiningApp.DisplayName} to the network: a second app, whose scope also covers medications",
                async () =>
                {
                    await PlatformCalls.RegisterAppAsync(
                        NetworkClient.BaseUrl,
                        JoiningAppSlug,
                        joiningApp.DisplayName,
                        joiningApp.AppScope!.Value,
                        joiningApp.AppDescription!
                    );
                    return true;
                }
            ),
            new Step(
                medicationCheck,
                "reads Sam's medications and kidney labs, checks who signed each one, and writes a signed note if a medicine needs a second look",
                async () =>
                {
                    if (await FindSamAsync() is not Guid patientId)
                    {
                        return false;
                    }
                    return await MedicationCheckCommand.RunAsync(["run", patientId.ToString()])
                        == 0;
                }
            ),
            // Sam checks in at Riverside, which needs its own link to write the visit later.
            Invite(riverside, "riverside", "RPC-1107"),
            // Dr. Rivera clicks "open EZEHR" in Sam's chart: Riverside's system makes the launch.
            new Step(
                riverside,
                "opens EZEHR for Dr. Rivera from Sam's chart (RPC-1107), for today's visit",
                async () =>
                {
                    if (
                        !Guid.TryParse(
                            Environment.GetEnvironmentVariable(ClinicianObjectIdVariable),
                            out Guid clinicianObjectId
                        )
                    )
                    {
                        Console.WriteLine(
                            $"  Set {ClinicianObjectIdVariable} to Dr. Rivera's object id in Riverside's Entra tenant, or run: ezehr-tools provider launch riverside RPC-1107 --clinician <object id>"
                        );
                        return false;
                    }
                    using HttpClient client = ProviderClient("riverside");
                    return await ProviderCalls.LaunchAsync(
                        client,
                        "RPC-1107",
                        clinicianObjectId,
                        $"visit-{DateTime.Now:yyyyMMdd}"
                    );
                }
            ),
            // The visit: Dr. Rivera opens that link and signs in; the share code, approval and reads happen on the
            // phone and the clinician page.
            Push(
                riverside,
                "riverside",
                "RPC-1107",
                "pushes today's visit: Dr. Rivera's plan and a nephrology referral"
            ),
            // Sam revokes Dr. Rivera's access; then the broker, then emergency access (browser only).
            Broker(
                brokerWithoutCertificate,
                $"asks for Sam's whole record, claiming \"{BrokerCommand.ClaimedPurpose}\"",
                withSelfSignedCertificate: false
            ),
            Broker(
                brokerWithOwnCertificate,
                "asks again, this time with a certificate",
                withSelfSignedCertificate: true
            ),
            new Step(
                riverside,
                "reads its own audit trail for Sam: what its clinicians and its system did, with the emergency reason",
                async () =>
                {
                    using HttpClient client = ProviderClient("riverside");
                    return await ProviderCalls.ReadAuditAsync(
                        client,
                        new OrganizationAuditQuery(
                            null,
                            null,
                            "RPC-1107",
                            null,
                            null,
                            ProviderCalls.DefaultAuditLimit
                        )
                    );
                }
            ),
        ];
    }

    private static Actor Provider(string slug, ConsoleColor color)
    {
        string name = OrgDirectory.Demo.FindByUri(OrgDirectory.ProviderUri(slug))!.DisplayName;
        return new Actor(name, "its EHR system", () => DescribeCertificate($"{slug}.pfx"), color);
    }

    private static string DescribeCertificate(string pfxFileName)
    {
        return PfxFile.Describe(ToolPaths.Pki(pfxFileName));
    }

    private static HttpClient ProviderClient(string slug)
    {
        return NetworkClient.Create(NetworkClient.BaseUrl, ToolPaths.Pki($"{slug}.pfx"));
    }

    private static HttpClient AppClient()
    {
        return NetworkClient.Create(NetworkClient.BaseUrl, ToolPaths.Pki("kidney-check.pfx"));
    }

    /// <summary>
    /// Kidney Check asks for medications with its labs-only grant. The refusal is the point, so a 403 counts as the
    /// step working; anything else is reported as a failure.
    /// </summary>
    private static async Task<bool> AskForMedicationsOutsideScopeAsync(
        HttpClient client,
        Guid patientId
    )
    {
        string path = $"api/network/patients/{patientId}/read-medications";
        NetworkLog.Request("GET", path, "medications, with a grant for lab results only");
        using HttpResponseMessage response = await client.GetAsync(path);
        if (response.IsSuccessStatusCode)
        {
            NetworkLog.WriteInColor(
                Color.Red,
                $"  ← {(int)response.StatusCode} {response.ReasonPhrase}   expected a refusal: the scope isn't enforced"
            );
            return false;
        }
        string body = await response.Content.ReadAsStringAsync();
        NetworkLog.WriteInColor(
            Color.Red,
            $"  ← {(int)response.StatusCode} {response.ReasonPhrase}   {body}".TrimEnd()
        );
        if (!body.Contains(nameof(DenyReason.ScopeDenied)))
        {
            Console.Error.WriteLine(
                $"  Expected ScopeDenied: is {KidneyCheckCommand.AppName} still turned on for Sam?"
            );
            return false;
        }
        Console.WriteLine(
            $"  Refused: Sam allowed {KidneyCheckCommand.AppName} lab results only. Sam's Activity shows the attempt."
        );
        return true;
    }

    private static void PrintBanner(int number, int count, Step step)
    {
        string label = $"━━ {number}/{count} ";
        Console.WriteLine();
        NetworkLog.WriteInColor(
            Color.FromConsoleColor(step.Actor.Color),
            label + new string('━', BannerWidth - label.Length)
        );
        NetworkLog.WriteInColor(
            Color.FromConsoleColor(step.Actor.Color),
            $"  {step.Actor.Name.ToUpperInvariant()} · {step.Actor.Role}"
        );
        Console.WriteLine($"  certificate: {step.Actor.Certificate()}");
        Console.WriteLine($"  {step.Actor.Name} {step.Action}");
        Console.WriteLine();
    }

    /// <summary>Shows the prompt, waits for one of the allowed keys, then erases the prompt so it isn't recorded.</summary>
    private static char WaitForKey(string prompt, string allowedKeys)
    {
        if (Console.IsInputRedirected)
        {
            // One key per line, for scripted rehearsals; a key this prompt doesn't take is ignored.
            while (Console.ReadLine() is string line)
            {
                char typed = line.Length == 0 ? '\r' : char.ToLowerInvariant(line[0]);
                if (allowedKeys.Contains(typed))
                {
                    return typed;
                }
            }
            return 'q';
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  {prompt}");
        Console.ResetColor();
        char key;
        do
        {
            ConsoleKeyInfo pressed = Console.ReadKey(intercept: true);
            key = pressed.Key == ConsoleKey.Enter ? '\r' : char.ToLowerInvariant(pressed.KeyChar);
        } while (!allowedKeys.Contains(key));
        Console.Write("\r" + new string(' ', prompt.Length + 2) + "\r");
        return key;
    }

    /// <summary>A network failure mid-demo shouldn't end the run: it's reported, and the step can be retried.</summary>
    private static async Task<bool> RunStepAsync(Step step)
    {
        try
        {
            return await step.RunAsync();
        }
        catch (ToolException exception)
        {
            NetworkLog.WriteInColor(Color.Red, $"  {exception.Message}");
            return false;
        }
    }
}
