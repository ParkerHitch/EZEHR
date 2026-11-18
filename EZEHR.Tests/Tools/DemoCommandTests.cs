namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class DemoCommandTests
{
    private static readonly Dictionary<string, string?> NoDatabase = new()
    {
        ["AZURE_SQL_CONNECTIONSTRING"] = null,
    };

    [Fact]
    public async Task ListNeedsNoDatabase()
    {
        ToolRun run = await ToolRunner.RunAsync(NoDatabase, "demo", "stage", "--list");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("1.", run.Output);
        Assert.Contains("Northwind", run.Output);
    }

    [Fact]
    public async Task FromOutOfRange()
    {
        ToolRun run = await ToolRunner.RunAsync("demo", "stage", "--from", "99");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("from 1 to", run.Output);
    }

    [Fact]
    public async Task ResetWithoutDatabase()
    {
        ToolRun run = await ToolRunner.RunAsync(NoDatabase, "demo", "reset", "--yes");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("AZURE_SQL_CONNECTIONSTRING", run.Output);
    }
}
