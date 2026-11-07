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

    // How often each FX/Metals timeframe is actually worth re-checking against
    // a quota-limited provider (Twelve Data) - matched to how fast that
    // timeframe's own candle closes, not to however often the caller happens
    // to run. Shared by SignalScanBackgroundService (always-on hosting) and
    // Program.cs's one-shot GitHub Actions scan, which otherwise re-scanned
    // every timeframe on every 15-minute cron tick regardless of whether a
    // slower timeframe's candle could possibly have changed - 4 FX instruments
    // x 5 timeframes x 96 runs/day is 1,920 calls/day against an 800/day/key
    // free-plan quota, exhausted in a matter of hours.
    public static TimeSpan FxPollInterval(Timeframe tf) => tf switch
    {
        Timeframe.M15 => TimeSpan.FromMinutes(10),
        Timeframe.H1 => TimeSpan.FromMinutes(20),
        Timeframe.H4 => TimeSpan.FromMinutes(45),
        Timeframe.Daily => TimeSpan.FromHours(2),
        Timeframe.Weekly => TimeSpan.FromHours(6),
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

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
