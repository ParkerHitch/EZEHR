using EZEHR.Tools.Calls;

namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class ProviderCommandTests
{
    [Fact]
    public void AuditQueryIncludesEveryFilter()
    {
        const string clinicianUri = "urn:entra:tenant:object";
        Guid patientId = Guid.NewGuid();
        DateTimeOffset since = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset until = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

        string query = ProviderCalls.AuditQuery(
            new OrganizationAuditQuery(clinicianUri, patientId, "LUC-20417", since, until, 50),
            afterId: 7
        );

        Assert.StartsWith("afterId=7&limit=50", query);
        Assert.Contains($"clinicianUri={Uri.EscapeDataString(clinicianUri)}", query);
        Assert.Contains($"patientId={patientId}", query);
        Assert.Contains("providerPatientId=LUC-20417", query);
        Assert.Contains($"since={Uri.EscapeDataString(since.ToString("O"))}", query);
        Assert.Contains($"until={Uri.EscapeDataString(until.ToString("O"))}", query);
    }

    [Fact]
    public async Task UnknownProviderIsAUsageError()
    {
        ToolRun run = await ToolRunner.RunAsync("provider", "invite", "nowhere", "LUC-1");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("lakeside", run.Output);
    }

    [Fact]
    public async Task AuditLimitOutOfRange()
    {
        ToolRun run = await ToolRunner.RunAsync("provider", "audit", "lakeside", "--limit", "0");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("1 to 500", run.Output);
    }

    [Fact]
    public async Task PushWithoutCertificate()
    {
        ToolRun run = await ToolRunner.RunAsync("provider", "push", "lakeside", "LUC-20417");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("certs issue", run.Output);
    }
}
