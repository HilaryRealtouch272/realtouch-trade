namespace RealtouchSmartTrade.Api.Strategy;

// DST-aware market sessions from real IANA time zones - never a fixed UTC
// offset, which is wrong for half of every year. Each session is its local
// weekday business window. The id is recorded on every candidate and trade so
// results can later be grouped by session.
public static class TradingSessions
{
    private record Session(string Name, string[] ZoneIds, int StartHour, int EndHour);

    private static readonly Session[] Sessions =
    {
        new("Sydney", new[] { "Australia/Sydney", "AUS Eastern Standard Time" }, 8, 17),
        new("Tokyo", new[] { "Asia/Tokyo", "Tokyo Standard Time" }, 9, 18),
        new("London", new[] { "Europe/London", "GMT Standard Time" }, 8, 17),
        new("NewYork", new[] { "America/New_York", "Eastern Standard Time" }, 8, 17),
    };

    private static TimeZoneInfo? Resolve(string[] ids)
    {
        foreach (var id in ids)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { /* try the next id (IANA on Linux, Windows ids on Windows) */ }
        }
        return null;
    }

    // e.g. "London+NewYork", or "OffHours" when none is open.
    public static string Describe(DateTime utc)
    {
        var open = new List<string>();
        foreach (var s in Sessions)
        {
            var zone = Resolve(s.ZoneIds);
            if (zone is null) continue;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (local.Hour >= s.StartHour && local.Hour < s.EndHour) open.Add(s.Name);
        }
        return open.Count == 0 ? "OffHours" : string.Join("+", open);
    }
}
