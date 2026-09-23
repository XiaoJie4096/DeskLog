namespace Riji.Core;

public static class DayRange
{
    public static string Today(DateTimeOffset now, TrackingSettings settings, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var date = local.Date;
        if (settings.NightMode && local.Hour < settings.DayStartHour) date = date.AddDays(-1);
        return date.ToString("yyyy-MM-dd");
    }

    public static (DateTimeOffset Start, DateTimeOffset End) Bounds(string day, TrackingSettings settings, TimeZoneInfo zone)
    {
        if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)) throw new ArgumentException("日期无效。");
        var hour = settings.NightMode ? settings.DayStartHour : 0;
        return (Boundary(date.ToDateTime(new TimeOnly(hour, 0)), zone),
            Boundary(date.AddDays(1).ToDateTime(new TimeOnly(hour, 0)), zone));
    }

    private static DateTimeOffset Boundary(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
