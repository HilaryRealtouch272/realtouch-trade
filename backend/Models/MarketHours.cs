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

    // Whether a result may go to Telegram now: 15m never does, and forex only while the market is open.
    public static bool TelegramAllowed(string symbol, string timeframe, DateTime utc)
    {
        if (timeframe == "15m") return false;
        var isFx = SetupCatalog.Instruments.Any(i => i.Symbol == symbol && i.Source == DataSource.TwelveData);
        return !(isFx && FxClosed(utc));
    }
}
