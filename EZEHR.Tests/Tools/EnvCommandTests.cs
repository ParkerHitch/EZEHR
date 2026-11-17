namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class EnvCommandTests
{
    [Fact]
    public async Task ReportsSiteDatabaseAndPki()
    {
        string pkiDirectory = ToolRunner.NewPkiDirectory();

        ToolRun run = await ToolRunner.RunAsync(
            new Dictionary<string, string?>
            {
                ["EZEHR_BASE_URL"] = "https://127.0.0.1:1",
                ["AZURE_SQL_CONNECTIONSTRING"] = null,
                [ToolRunner.PkiDirectoryVariable] = pkiDirectory,
            },
            "env"
        );

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("https://127.0.0.1:1", run.Output);
        Assert.Contains("no answer", run.Output);
        Assert.Contains("AZURE_SQL_CONNECTIONSTRING", run.Output);
        Assert.Contains(pkiDirectory, run.Output);
    }

    [Fact]
    public async Task OrgListShowsMissingCertificates()
    {
        ToolRun run = await ToolRunner.RunAsync("org", "list");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("lakeside", run.Output);
        Assert.Contains("kidney-check", run.Output);
        Assert.Contains("missing", run.Output);
    }
}
