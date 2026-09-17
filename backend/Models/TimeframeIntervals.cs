namespace RealtouchSmartTrade.Api.Models;

// Shared timeframe->provider-interval mappings so the setup catalog can be
// generated for all five timeframes per instrument instead of one hardcoded
// timeframe per instrument.
public static class TimeframeIntervals
{
    // Bybit v5 kline interval codes: minutes as strings, "D" for daily, "W" for weekly.
    public static string BybitCode(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => "W",
        Timeframe.Daily => "D",
        Timeframe.H4 => "240",
        Timeframe.H1 => "60",
        Timeframe.M15 => "15",
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    public static string TwelveDataCode(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => "1week",
        Timeframe.Daily => "1day",
        Timeframe.H4 => "4h",
        Timeframe.H1 => "1h",
        Timeframe.M15 => "15min",
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    // Matches the frontend's timeframe filter labels exactly ("Weekly","Daily","4H","1H","15m").
    public static string Label(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => "Weekly",
        Timeframe.Daily => "Daily",
        Timeframe.H4 => "4H",
        Timeframe.H1 => "1H",
        Timeframe.M15 => "15m",
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    public static readonly Timeframe[] All = { Timeframe.Weekly, Timeframe.Daily, Timeframe.H4, Timeframe.H1, Timeframe.M15 };

    // Inverse of Label(), for parsing a ?timeframe= query string back to the enum.
    public static Timeframe? ParseLabel(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        "weekly" => Timeframe.Weekly,
        "daily" => Timeframe.Daily,
        "4h" => Timeframe.H4,
        "1h" => Timeframe.H1,
        "15m" => Timeframe.M15,
        _ => null
    };
}
