using EZEHR.Tools.Calls;
using EZEHR.Tools.Net;

namespace EZEHR.Tools.Commands;

/// <summary>
/// The data broker from the video: asks for a patient's whole record claiming "treatment", with no certificate
/// or with its own self-made one. The server refuses both and logs each attempt for the patient.
/// </summary>
public static class BrokerCommand
{
    public const string Usage = """
          broker request <patientId> --cert none|self
        """;

    public const string ClaimedPurpose = "treatment";

    public static async Task<int> RunAsync(string[] args)
    {
        string? certificateMode = CommandLine.Option(args, "--cert");
        if (
            args.Length < 2
            || args[0] != "request"
            || !Guid.TryParse(args[1], out Guid patientId)
            || certificateMode is not ("none" or "self")
        )
        {
            Console.Error.WriteLine($"usage:\n{Usage}");
            return 1;
        }
        await RequestAsync(patientId, certificateMode == "self");
        return 0;
    }

    /// <summary>Asks for the full record. Refused is the expected answer, so this doesn't report failure.</summary>
    public static async Task RequestAsync(Guid patientId, bool withSelfSignedCertificate)
    {
        string? pfxPath = withSelfSignedCertificate ? ToolPaths.Pki("broker.pfx") : null;
        using HttpClient client = NetworkClient.Create(NetworkClient.BaseUrl, pfxPath);
        string path = $"api/network/patients/{patientId}/read-full-record?purpose={ClaimedPurpose}";
        NetworkLog.Request(
            "GET",
            path,
            withSelfSignedCertificate
                ? $"certificate: self-signed CN={CertificateIssuance.BrokerCommonName}"
                : "certificate: none"
        );
        using HttpResponseMessage response = await client.GetAsync(path);
        if (response.IsSuccessStatusCode)
        {
            NetworkLog.Succeeded(response, "the broker got the record");
            return;
        }
        await NetworkLog.FailedAsync(response);
    }
}
