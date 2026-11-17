using EZEHR.Services;

namespace EZEHR.Tests.Services;

public class MedicationRuleTests
{
    private static readonly IReadOnlyList<(DateOnly Date, double Egfr, string Source)> SeedLabs =
    [
        (new DateOnly(2024, 9, 12), 96, "Maple Street Family Medicine"),
        (new DateOnly(2025, 3, 4), 88, "Northwind Workplace Health"),
        (new DateOnly(2026, 9, 15), 63, "Lakeside Urgent Care"),
    ];

    [Fact]
    public void Painkiller_with_falling_kidney_function_flags()
    {
        MedicationResult result = MedicationRule.Evaluate(
            ["lisinopril 10 mg daily", "Ibuprofen 600 mg three times daily as needed"],
            SeedLabs
        );

        Assert.Equal(new MedicationResult(true, "ibuprofen", 34), result);
    }

    [Fact]
    public void Falling_kidney_function_without_a_painkiller_does_not_flag()
    {
        MedicationResult result = MedicationRule.Evaluate(["lisinopril 10 mg daily"], SeedLabs);

        Assert.Equal(new MedicationResult(false, null, 34), result);
    }

    [Fact]
    public void Painkiller_with_steady_kidney_function_does_not_flag()
    {
        MedicationResult result = MedicationRule.Evaluate(
            ["naproxen 250 mg"],
            [
                (new DateOnly(2024, 9, 12), 96, "Maple Street Family Medicine"),
                (new DateOnly(2026, 9, 15), 90, "Lakeside Urgent Care"),
            ]
        );

        Assert.False(result.Flag);
        Assert.Equal("naproxen", result.Medication);
    }

    [Fact]
    public void No_labs_does_not_flag()
    {
        Assert.False(MedicationRule.Evaluate(["ibuprofen 600 mg"], []).Flag);
    }
}
