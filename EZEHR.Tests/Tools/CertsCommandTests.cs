namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class CertsCommandTests
{
    [Fact]
    public async Task IssueWritesEveryDemoCertificate()
    {
        string pkiDirectory = ToolRunner.NewPkiDirectory();

        ToolRun run = await ToolRunner.RunAsync(
            new Dictionary<string, string?> { [ToolRunner.PkiDirectoryVariable] = pkiDirectory },
            "certs",
            "issue"
        );

        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(pkiDirectory, "lakeside.pfx")));
        Assert.True(File.Exists(Path.Combine(pkiDirectory, "kidney-check.pfx")));
        Assert.True(File.Exists(Path.Combine(pkiDirectory, "anchors.pem")));
        Assert.True(File.Exists(Path.Combine(pkiDirectory, "intermediates.pem")));
    }

    [Fact]
    public async Task PlatformPrintsThePin()
    {
        ToolRun run = await ToolRunner.RunAsync("certs", "platform");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("App__PlatformCertificateThumbprint", run.Output);
    }

    [Fact]
    public async Task BadSlugIsAUsageError()
    {
        ToolRun run = await ToolRunner.RunAsync("certs", "provider", "Bad_Slug");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("lowercase", run.Output);
    }

    [Fact]
    public async Task FullScopeIsRefused()
    {
        ToolRun run = await ToolRunner.RunAsync(
            "platform",
            "register-app",
            "x",
            "--name",
            "X",
            "--scope",
            "Full",
            "--description",
            "d"
        );

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("LabsAndNotes", run.Output);
    }

    [Fact]
    public async Task RegisterWithoutPlatformCertificate()
    {
        ToolRun run = await ToolRunner.RunAsync(
            "platform",
            "register-provider",
            "harbor",
            "--name",
            "Harbor"
        );

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("certs platform", run.Output);
    }

    [Fact]
    public async Task UnknownAuthority()
    {
        ToolRun run = await ToolRunner.RunAsync("certs", "issue", "--ca", "nope");

        Assert.Equal(2, run.ExitCode);
    }
}
