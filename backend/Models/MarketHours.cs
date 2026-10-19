namespace RealtouchSmartTrade.Api.Models;

// Session rules that decide when a market can produce real prices.
public static class MarketHours
{
    // Spot FX is closed from Friday 22:00 UTC until Sunday 22:00 UTC.
    public static bool FxClosed(DateTime utc) => utc.DayOfWeek switch
    {
        DayOfWeek.Saturday => true,
        DayOfWeek.Sunday => utc.Hour < 22,
        DayOfWeek.Friday => utc.Hour >= 22,
        _ => false
    };

    // 15m is dashboard-only everywhere, except this explicit carve-out - an owner decision, not a
    // general rule. Add further symbols here only on the same kind of explicit instruction.
    private static readonly HashSet<string> FifteenMinuteAlertsAllowed =
        new(StringComparer.OrdinalIgnoreCase) { "EUR/USD", "GBP/USD", "GBP/JPY" };

    // Within that carve-out, grade A specifically is still withheld on 15m (owner decision) -
    // B and A+ still alert there. This has no effect on any other timeframe.
    private static bool IsWithheldFifteenMinuteGrade(string? grade) => string.Equals(grade, "A", StringComparison.OrdinalIgnoreCase);

    // Whether a result may go to Telegram now: 15m never does except the carve-out above (and
    // never for grade A even there), and forex only while the market is open.
    public static bool TelegramAllowed(string symbol, string timeframe, string? grade, DateTime utc)
    {
        if (timeframe == "15m" && (!FifteenMinuteAlertsAllowed.Contains(symbol) || IsWithheldFifteenMinuteGrade(grade))) return false;
        var isFx = SetupCatalog.Instruments.Any(i => i.Symbol == symbol && i.Source == DataSource.TwelveData);
        return !(isFx && FxClosed(utc));
    }
}
