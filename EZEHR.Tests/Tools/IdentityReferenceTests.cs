using EZEHR.Tools;
using EZEHR.Tools.Cli;

namespace EZEHR.Tests.Tools;

[Collection(ToolConsoleCollection.Name)]
public sealed class IdentityReferenceTests
{
    [Fact]
    public void ParsesEachForm()
    {
        WithEmptyPki(() =>
        {
            Assert.Null(IdentityReference.Parse("none").PfxPath);
            Assert.EndsWith("broker.pfx", IdentityReference.Parse("broker").PfxPath);
            Assert.EndsWith("lakeside.pfx", IdentityReference.Parse("lakeside").PfxPath);
            Assert.Equal(Path.GetFullPath("./x.pfx"), IdentityReference.Parse("./x.pfx").PfxPath);
            FormatException error = Assert.Throws<FormatException>(() =>
                IdentityReference.Parse("harbor")
            );
            Assert.Contains("lakeside", error.Message);
        });
    }

    [Fact]
    public void MissingPfxNamesTheCertsCommand()
    {
        WithEmptyPki(() =>
        {
            ToolException demo = Assert.Throws<ToolException>(() =>
                IdentityReference.Parse("lakeside").RequirePfx()
            );
            Assert.Contains("certs issue", demo.Message);
            ToolException broker = Assert.Throws<ToolException>(() =>
                IdentityReference.Parse("broker").RequirePfx()
            );
            Assert.Contains("certs broker", broker.Message);
        });
    }

    [Fact]
    public void IssuedButUnregisteredSlugIsAnIdentity()
    {
        WithEmptyPki(() =>
        {
            File.WriteAllBytes(EZEHR.Tools.ToolPaths.Pki("harbor.pfx"), []);

            Assert.EndsWith("harbor.pfx", IdentityReference.Parse("harbor").PfxPath);
        });
    }

    [Fact]
    public void RelativePkiDirectoryIsResolvedNow()
    {
        string? previous = Environment.GetEnvironmentVariable(ToolRunner.PkiDirectoryVariable);
        Environment.SetEnvironmentVariable(ToolRunner.PkiDirectoryVariable, ".pki-relative");
        try
        {
            Assert.True(Path.IsPathRooted(EZEHR.Tools.ToolPaths.PkiDirectory));
            Assert.Equal(Path.GetFullPath(".pki-relative"), EZEHR.Tools.ToolPaths.PkiDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ToolRunner.PkiDirectoryVariable, previous);
        }
    }

    [Fact]
    public void MissingPfxPathSuggestsNoCommand()
    {
        ToolException error = Assert.Throws<ToolException>(() =>
            IdentityReference.Parse("./nowhere/missing.pfx").RequirePfx()
        );

        Assert.Contains("missing.pfx not found", error.Message);
        Assert.DoesNotContain("certs", error.Message);
    }

    private static void WithEmptyPki(Action test)
    {
        string? previous = Environment.GetEnvironmentVariable(ToolRunner.PkiDirectoryVariable);
        Environment.SetEnvironmentVariable(
            ToolRunner.PkiDirectoryVariable,
            ToolRunner.NewPkiDirectory()
        );
        try
        {
            test();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ToolRunner.PkiDirectoryVariable, previous);
        }
    }
}
