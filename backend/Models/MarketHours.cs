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

    // Owner decision, temporary: these symbols are paused on Telegram entirely (every timeframe,
    // every grade) - still tracked and shown on the dashboard as normal. Remove a symbol from
    // this set to resume alerting it.
    private static readonly HashSet<string> TelegramPaused = new(StringComparer.OrdinalIgnoreCase) { "ETH/USDT" };

    // Owner decision, temporary: XAU/USD is held back from Telegram only during Lagos mornings
    // (00:00-12:00 WAT, midnight to noon - i.e. 23:00 UTC the previous day through 10:59 UTC,
    // since WAT has no DST and is a fixed UTC+1). Outside that window it alerts normally. This
    // never touches qualification or the ledger - gold setups keep qualifying and logging
    // exactly as before; only the Telegram send is held. Computed via the WAT hour (not a
    // UTC-hour range) so the wrap across UTC midnight is never a source of a sign/boundary bug.
    private static bool IsPausedGoldMorning(string symbol, DateTime utc) =>
        string.Equals(symbol, "XAU/USD", StringComparison.OrdinalIgnoreCase) && (utc.Hour + 1) % 24 < 12;

    // Whether a result may go to Telegram now: paused symbols never do, the gold-morning hold
    // above never does, 15m never does except the carve-out above (and never for grade A even
    // there), and forex only while the market is open.
    public static bool TelegramAllowed(string symbol, string timeframe, string? grade, DateTime utc)
        => TelegramBlockReason(symbol, timeframe, grade, utc) is null;

    public static string? TelegramBlockReason(string symbol, string timeframe, string? grade, DateTime utc)
    {
        if (TelegramPaused.Contains(symbol)) return "Telegram alerts are paused for this symbol";
        if (IsPausedGoldMorning(symbol, utc)) return "Gold Telegram alerts pause before noon Lagos time";
        if (timeframe == "15m" && !FifteenMinuteAlertsAllowed.Contains(symbol)) return "This 15m timeframe is dashboard-only";
        if (timeframe == "15m" && IsWithheldFifteenMinuteGrade(grade)) return "Grade A alerts on 15m are dashboard-only";
        // WTI (Yahoo-sourced) reuses the same Friday-22:00/Sunday-22:00 UTC closed window as an
        // approximation of its real CME Globex session - not exact (Globex has its own brief
        // daily settlement halt and slightly different weekend open), but close enough that a
        // paper-trade Telegram alert never fires while the real market is plainly shut.
        var sessionGated = SetupCatalog.Instruments.Any(i => i.Symbol == symbol && i.Source is DataSource.TwelveData or DataSource.Yahoo);
        return sessionGated && FxClosed(utc) ? "FX Telegram alerts pause while the market is closed" : null;
    }
}
