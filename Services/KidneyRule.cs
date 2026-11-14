namespace EZEHR.Services;

/// <summary>Flag is whether to write the note; PercentDrop is earliest to latest, rounded; ProviderCount is distinct sources.</summary>
public sealed record KidneyResult(bool Flag, int PercentDrop, int ProviderCount);

/// <summary>
/// The Kidney Check app's whole rule: flag when at least 2 eGFR results from at least 2 providers show a drop of
/// more than 25% from the earliest to the latest. Deliberately not a clinical guideline, and never a diagnosis.
/// </summary>
public static class KidneyRule
{
    public const double FlagAbovePercentDrop = 25;

    public static KidneyResult Evaluate(
        IReadOnlyList<(DateOnly Date, double Egfr, string Source)> points
    )
    {
        int providerCount = points.Select(point => point.Source).Distinct().Count();
        if (points.Count < 2)
        {
            return new KidneyResult(false, 0, providerCount);
        }

        List<(DateOnly Date, double Egfr, string Source)> byDate = points
            .OrderBy(point => point.Date)
            .ToList();
        double earliest = byDate[0].Egfr;
        double latest = byDate[^1].Egfr;
        double percentDrop = earliest > 0 ? (earliest - latest) / earliest * 100 : 0;
        bool flag = providerCount >= 2 && percentDrop > FlagAbovePercentDrop;
        return new KidneyResult(
            flag,
            (int)Math.Round(percentDrop, MidpointRounding.AwayFromZero),
            providerCount
        );
    }
}
