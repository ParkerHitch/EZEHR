using EZEHR.Tools.Commands.Demo;

namespace EZEHR.Tests.Tools;

public sealed class StageKeysTests
{
    [Fact]
    public void ReadsOneKeyPerLine()
    {
        using StringReader input = new("\nx\ns\n");

        char first = StageKeys.ReadScripted(input, "\rsq");
        char second = StageKeys.ReadScripted(input, "\rsq");
        char atEnd = StageKeys.ReadScripted(input, "\rsq");

        Assert.Equal('\r', first);
        Assert.Equal('s', second);
        Assert.Equal('q', atEnd);
    }

    [Fact]
    public void EraseReturnsToTheStartOfTheLine()
    {
        using StringWriter output = new();

        StageKeys.Erase(output, "Enter: send");

        Assert.Equal("\r" + new string(' ', "Enter: send".Length + 2) + "\r", output.ToString());
    }
}
