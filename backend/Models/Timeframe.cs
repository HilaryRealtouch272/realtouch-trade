namespace RealtouchSmartTrade.Api.Models;

public enum Timeframe { Weekly, Daily, H4, H1, M15 }

public static class TimeframeConfig
{
    // Section 5: pivot confirmation - candles required on each side of a swing point
    // before it is considered confirmed (non-repainting).
    public static int PivotBars(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => 2,
        Timeframe.Daily => 3,
        Timeframe.H4 => 3,
        Timeframe.H1 => 3,
        Timeframe.M15 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    public static TimeSpan Duration(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => TimeSpan.FromDays(7),
        Timeframe.Daily => TimeSpan.FromDays(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };
}
