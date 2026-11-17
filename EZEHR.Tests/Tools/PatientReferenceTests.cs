using EZEHR.Tools.Cli;

namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class PatientReferenceTests
{
    [Fact]
    public void ParsesEachForm()
    {
        Guid patientId = Guid.NewGuid();

        Assert.Equal(patientId, PatientReference.Parse(patientId.ToString()).PatientId);
        Assert.True(PatientReference.Parse("sam").IsSam);
        PatientReference byChart = PatientReference.Parse("lakeside:LUC-20417");
        Assert.Equal("lakeside", byChart.ProviderSlug);
        Assert.Equal("LUC-20417", byChart.Chart);
        FormatException error = Assert.Throws<FormatException>(() =>
            PatientReference.Parse("nope")
        );
        Assert.Contains("lakeside:LUC-20417", error.Message);
    }

    [Fact]
    public async Task NamedPatientWithoutDatabaseSaysHowToSetIt()
    {
        ToolRun run = await ToolRunner.RunAsync(
            new Dictionary<string, string?> { ["AZURE_SQL_CONNECTIONSTRING"] = null },
            "patient",
            "status",
            "sam"
        );

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("AZURE_SQL_CONNECTIONSTRING", run.Output);
        Assert.Contains("scripts/local.env", run.Output);
    }

    [Fact]
    public async Task GuidNeedsNoDatabase()
    {
        Guid patientId = Guid.NewGuid();
        string? previous = Environment.GetEnvironmentVariable("AZURE_SQL_CONNECTIONSTRING");
        Environment.SetEnvironmentVariable("AZURE_SQL_CONNECTIONSTRING", null);
        try
        {
            Assert.Equal(
                patientId,
                await PatientReference.Parse(patientId.ToString()).ResolveAsync()
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_SQL_CONNECTIONSTRING", previous);
        }
    }

    [Fact]
    public async Task MalformedPatientIsAUsageError()
    {
        ToolRun run = await ToolRunner.RunAsync("patient", "status", "not-a-patient");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("lakeside:LUC-20417", run.Output);
    }
}
