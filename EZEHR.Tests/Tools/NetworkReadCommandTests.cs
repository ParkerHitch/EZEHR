using EZEHR.Tools.Calls;

namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class NetworkReadCommandTests
{
    private static readonly string PatientId = Guid.NewGuid().ToString();

    [Fact]
    public void PathForEachRecord()
    {
        Guid patientId = Guid.Parse(PatientId);

        Assert.Equal(
            $"api/network/patients/{patientId}/read-egfr-labs",
            RecordReads.PathFor(patientId, RecordKind.EgfrLabs, null)
        );
        Assert.EndsWith(
            "read-full-record?purpose=treatment",
            RecordReads.PathFor(patientId, RecordKind.FullRecord, "treatment")
        );
    }

    [Fact]
    public async Task PurposeOnlyForFullRecord()
    {
        ToolRun run = await ToolRunner.RunAsync(
            "network",
            "read",
            PatientId,
            "egfr-labs",
            "--as",
            "none",
            "--purpose",
            "x"
        );

        Assert.Equal(2, run.ExitCode);
    }

    [Fact]
    public async Task UnreachableServerSaysWhichUrl()
    {
        ToolRun run = await ToolRunner.RunAsync(
            "network",
            "read",
            PatientId,
            "full-record",
            "--as",
            "none",
            "--base-url",
            "https://127.0.0.1:1"
        );

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("couldn't reach EZEHR at https://127.0.0.1:1", run.Output);
    }

    [Fact]
    public async Task UnknownRecordIsAUsageError()
    {
        ToolRun run = await ToolRunner.RunAsync(
            "network",
            "read",
            PatientId,
            "labs",
            "--as",
            "none"
        );

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("egfr-labs", run.Output);
    }
}
