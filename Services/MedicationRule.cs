namespace EZEHR.Services;

/// <summary>Flag is whether to write the note; Medication is the one that matched; PercentDrop is the eGFR drop, earliest to latest, rounded.</summary>
public sealed record MedicationResult(bool Flag, string? Medication, int PercentDrop);

/// <summary>
/// The Medication Check app's whole rule: flag when the patient takes a common anti-inflammatory painkiller and
/// their eGFR has dropped more than 25% from the earliest to the latest result. It needs two kinds of data that
/// often sit with different providers. Deliberately not a clinical guideline, and never a diagnosis.
/// </summary>
public static class MedicationRule
{
    private static readonly string[] AntiInflammatoryPainkillers =
    [
        "ibuprofen",
        "naproxen",
        "diclofenac",
        "ketorolac",
        "meloxicam",
        "indomethacin",
        "celecoxib",
    ];

    /// <summary>
    /// <paramref name="activeMedications"/> is each active medication as its record names it, like
    /// "ibuprofen 600 mg three times daily as needed".
    /// </summary>
    public static MedicationResult Evaluate(
        IReadOnlyList<string> activeMedications,
        IReadOnlyList<(DateOnly Date, double Egfr, string Source)> egfrPoints
    )
    {
        string? medication = AntiInflammatoryPainkillers.FirstOrDefault(name =>
            activeMedications.Any(active =>
                active.Contains(name, StringComparison.OrdinalIgnoreCase)
            )
        );
        int percentDrop = KidneyRule.Evaluate(egfrPoints).PercentDrop;
        bool flag = medication is not null && percentDrop > KidneyRule.FlagAbovePercentDrop;
        return new MedicationResult(flag, medication, percentDrop);
    }
}
