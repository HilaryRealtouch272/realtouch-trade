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
}
