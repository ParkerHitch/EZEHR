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
}
