using System.Globalization;

namespace EZEHR.Services;

/// <summary>Times shown on screen, in the clinic's time zone (AppOptions.TimeZone), like "5:00 PM".</summary>
public static class LocalTime
{
    public static string ShortTime(DateTimeOffset at, string timeZoneId)
    {
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return TimeZoneInfo
            .ConvertTime(at, timeZone)
            .ToString("h:mm tt", CultureInfo.InvariantCulture);
    }
}
