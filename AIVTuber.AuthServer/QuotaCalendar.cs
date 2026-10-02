using System.Globalization;

namespace AIVTuber.AuthServer;

/// <summary>Quota days run from 06:00 to 06:00 Beijing time (UTC+8), so a late-night stream
/// belongs to the day it started on.</summary>
public static class QuotaCalendar
{
    private static readonly TimeSpan Zone = TimeSpan.FromHours(8);
    public const int ResetHour = 6;

    public static string DayOf(DateTimeOffset now) =>
        now.ToOffset(Zone).AddHours(-ResetHour).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTimeOffset ResetsAfter(DateTimeOffset now)
    {
        var shifted = now.ToOffset(Zone).AddHours(-ResetHour);
        return new DateTimeOffset(shifted.Date.AddDays(1).AddHours(ResetHour), Zone);
    }
}
