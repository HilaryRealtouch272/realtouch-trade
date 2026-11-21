using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Cash-index daily trend sleeve. No target and no arbitrary time expiry.
public static class SpxTrendRules
{
    public const string Symbol = "SPX/USD";
    public const string ExitMode = "SpxTrend";
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateTime SessionClose(DateTime sessionDate) => TimeZoneInfo.ConvertTimeToUtc(
        DateTime.SpecifyKind(sessionDate.Date.AddHours(16).AddMinutes(15), DateTimeKind.Unspecified), NewYork);

    public static DateTime SessionDate(DateTime timestampUtc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc), NewYork).Date;

    public static bool FreshEntry(NormalizedCandle bar, DateTime now) =>
        bar.OpenTimeUtc.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && MarketHours.NewEntriesAllowed(Symbol, now)
        && bar.IsComplete && bar.CloseTimeUtc <= now && now - bar.CloseTimeUtc <= TimeSpan.FromMinutes(45);

    public static VtbClosedTrade? Settle(VtbPosition position, IReadOnlyList<NormalizedCandle> daily, DateTime now)
    {
        var ordered = daily.OrderBy(c => c.OpenTimeUtc).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var bar = ordered[i];
            if (bar.OpenTimeUtc <= position.EntryTimeUtc || bar.OpenTimeUtc > now) continue;
            var stop = bar.Low <= position.Stop;
            var channelExit = i >= 50 && bar.IsComplete && bar.CloseTimeUtc <= now
                && bar.Close < ordered.Skip(i - 50).Take(50).Min(c => c.Low);
            if (!stop && !channelExit) continue;
            var exit = stop ? Math.Min(position.Stop, bar.Open) : bar.Close;
            return new(VtbSide.Long, position.EntryTimeUtc, position.EntryRef, position.Stop, 0m,
                position.Risk, bar.CloseTimeUtc <= now ? bar.CloseTimeUtc : now, exit,
                stop ? VtbExitReason.Stop : VtbExitReason.Channel, (exit - position.EntryRef) / position.Risk);
        }
        return null;
    }
}
