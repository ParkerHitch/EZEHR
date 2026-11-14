using EZEHR.Services;

namespace EZEHR.Tests.Services;

public class KidneyRuleTests
{
    [Fact]
    public void Seed_series_flags_34_percent_3_providers()
    {
        // The seed series, out of order: the rule compares the earliest and latest by date.
        KidneyResult result = KidneyRule.Evaluate([
            (new DateOnly(2026, 3, 10), 72, "Northwind Workplace Health"),
            (new DateOnly(2024, 9, 12), 96, "Maple Street Family Medicine"),
            (new DateOnly(2026, 9, 15), 63, "Lakeside Urgent Care"),
            (new DateOnly(2025, 3, 4), 88, "Northwind Workplace Health"),
            (new DateOnly(2025, 8, 21), 81, "Lakeside Urgent Care"),
        ]);

        Assert.Equal(new KidneyResult(true, 34, 3), result);
    }

    [Fact]
    public void Single_source_no_flag()
    {
        KidneyResult result = KidneyRule.Evaluate([
            (new DateOnly(2024, 9, 12), 96, "Lakeside Urgent Care"),
            (new DateOnly(2026, 9, 15), 63, "Lakeside Urgent Care"),
        ]);

        Assert.False(result.Flag);
        Assert.Equal(1, result.ProviderCount);
    }

    [Fact]
    public void Drop_25_exactly_no_flag()
    {
        KidneyResult result = KidneyRule.Evaluate([
            (new DateOnly(2025, 1, 1), 100, "Maple Street Family Medicine"),
            (new DateOnly(2026, 1, 1), 75, "Lakeside Urgent Care"),
        ]);

        Assert.Equal(new KidneyResult(false, 25, 2), result);
    }

    [Fact]
    public void One_point_no_flag()
    {
        KidneyResult result = KidneyRule.Evaluate([
            (new DateOnly(2026, 9, 15), 63, "Lakeside Urgent Care"),
        ]);

        Assert.False(result.Flag);
    }
}
