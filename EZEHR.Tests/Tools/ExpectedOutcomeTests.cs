using System.Net;
using EZEHR.Tools.Calls;

namespace EZEHR.Tests.Tools;

public sealed class ExpectedOutcomeTests
{
    [Fact]
    public void MatchesStatusReasonAndOk()
    {
        ReadOutcome allowed = new(HttpStatusCode.OK, null, [], "{}");
        ReadOutcome scopeDenied = new(HttpStatusCode.Forbidden, "ScopeDenied", [], "");
        ReadOutcome noGrant = new(HttpStatusCode.Forbidden, "NoGrant", [], "");

        Assert.True(ExpectedOutcome.Parse("ok").Matches(allowed));
        Assert.False(ExpectedOutcome.Parse("ok").Matches(scopeDenied));
        Assert.True(ExpectedOutcome.Parse("403").Matches(scopeDenied));
        Assert.True(ExpectedOutcome.Parse("403").Matches(noGrant));
        Assert.True(ExpectedOutcome.Parse("ScopeDenied").Matches(scopeDenied));
        Assert.False(ExpectedOutcome.Parse("ScopeDenied").Matches(noGrant));
        FormatException error = Assert.Throws<FormatException>(() =>
            ExpectedOutcome.Parse("Nonsense")
        );
        Assert.Contains("ScopeDenied", error.Message);
    }
}
