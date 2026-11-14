using System.Globalization;

namespace EZEHR.Services;

/// <summary>Times as the patient and clinician read them, in AppOptions.TimeZone.</summary>
public static class LocalTime
{
    /// <summary>"5:00 PM"</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo zone)
    {
        return TimeZoneInfo.ConvertTime(at, zone).ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    /// <summary>"Nov 14"</summary>
    public static string Day(DateTimeOffset at, TimeZoneInfo zone)
    {
        return TimeZoneInfo.ConvertTime(at, zone).ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
