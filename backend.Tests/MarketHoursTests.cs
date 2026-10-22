using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class MarketHoursTests
{
    [Theory]
    [InlineData("2026-10-03T12:00:00Z", true)]   // Saturday
    [InlineData("2026-10-04T21:59:00Z", true)]   // Sunday before the 22:00 reopen
    [InlineData("2026-10-04T22:00:00Z", false)]  // Sunday reopen
    [InlineData("2026-10-02T21:59:00Z", false)]  // Friday before the 22:00 close
    [InlineData("2026-10-02T22:00:00Z", true)]   // Friday close
    [InlineData("2026-10-06T12:00:00Z", false)]  // Tuesday
    public void FxIsClosedFromFridayTenPmUtcUntilSundayTenPmUtc(string utc, bool closed)
    {
        Assert.Equal(closed, MarketHours.FxClosed(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));
    }

    [Fact]
    public void AGradeChangeIsLabelledInTheTelegramMessage()
    {
        var signal = new SignalResult("BTC/USDT:1H:x", "v", "BTC/USDT", "crypto", "Coinbase", "1H", "15m", SetupDirection.Long, MarketCondition.Ranging, SetupModelType.RangeBoundaryRejection,
            SignalState.Triggered, 96, "A+", DateTime.UtcNow, DateTime.UtcNow, 83289m, 83242m, 83335m, 83289m, true, 83058m, 83519m, 84451m, 85613m, 5m, 0.5m, 100m, 1m, Array.Empty<KeyLevel>(), Array.Empty<ConfluenceFamilyScore>(), "", "", "", "", "", "", "");
        Assert.Contains("Grade changed A → A+", TelegramSignalFormatter.Format(signal, true, "A → A+"));
        Assert.DoesNotContain("Grade changed", TelegramSignalFormatter.Format(signal, true));
    }

    [Theory]
    [InlineData("15m", false)]
    [InlineData("1H", true)]
    [InlineData("Daily", true)]
    public void OutcomeUpdatesForFifteenMinuteTradesAreNotSent(string timeframe, bool send)
    {
        var entry = new QualificationLogEntry("x", "BTC/USDT", timeframe, "Long", "TrendContinuationPullback", "A", 86, 1m, 0.9m, 1.1m, 1.2m, 1.4m, 2m, DateTime.UtcNow, DateTime.UtcNow, "Tp1Hit", null, null, null, null);
        Assert.Equal(send, SignalLogService.ShouldSendOutcome(entry, new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc)));
    }

    // Owner decision: EUR/USD, GBP/USD and GBP/JPY are the explicit carve-out from "15m is
    // dashboard-only" - everything else still never alerts on 15m, at any grade.
    private static readonly DateTime AWeekdayNoon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("EUR/USD")]
    [InlineData("GBP/USD")]
    // GBP/JPY is in this carve-out too, but is separately paused on Telegram entirely right
    // now (see the yen-pair tests below) - it's covered there, not here.
    public void TheCarvedOutPairsAllowFifteenMinuteSignalsOnTelegram(string symbol)
    {
        Assert.True(MarketHours.TelegramAllowed(symbol, "15m", "B", AWeekdayNoon));
        Assert.True(MarketHours.TelegramAllowed(symbol, "15m", "A+", AWeekdayNoon));
    }

    [Fact]
    public void EverySymbolOutsideTheCarveOutStillNeverAlertsOnFifteenMinutes()
    {
        Assert.False(MarketHours.TelegramAllowed("BTC/USDT", "15m", "A+", AWeekdayNoon));
        Assert.False(MarketHours.TelegramAllowed("XAU/USD", "15m", "A+", AWeekdayNoon));
    }

    [Theory]
    [InlineData("EUR/USD")]
    [InlineData("GBP/USD")]
    [InlineData("GBP/JPY")]
    public void GradeAIsWithheldOnFifteenMinutesEvenForCarvedOutPairs(string symbol)
    {
        // Owner decision: no grade-A 15m alerts, even for the pairs that otherwise alert on 15m.
        Assert.False(MarketHours.TelegramAllowed(symbol, "15m", "A", AWeekdayNoon));
        Assert.False(MarketHours.TelegramAllowed(symbol, "15m", "a", AWeekdayNoon)); // case-insensitive
    }

    [Fact]
    public void GradeAStillAlertsNormallyOnTimeframesAboveFifteenMinutes()
    {
        Assert.True(MarketHours.TelegramAllowed("EUR/USD", "1H", "A", AWeekdayNoon));
        Assert.True(MarketHours.TelegramAllowed("BTC/USDT", "4H", "A", AWeekdayNoon));
    }

    [Fact]
    public void TheCarveOutStillPausesOverTheWeekend()
    {
        // The carve-out lifts the 15m gate, not the separate FX-closed gate.
        var saturday = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(MarketHours.TelegramAllowed("EUR/USD", "15m", "B", saturday));
    }

    // Owner decision, temporary: these symbols are paused on Telegram entirely.
    [Theory]
    [InlineData("GBP/JPY")]
    [InlineData("USD/JPY")]
    [InlineData("ETH/USDT")]
    [InlineData("XAU/USD")]
    public void PausedSymbolsAreHeldBackOnTelegramAtEveryTimeframeAndGrade(string symbol)
    {
        Assert.False(MarketHours.TelegramAllowed(symbol, "1H", "A+", AWeekdayNoon));
        Assert.False(MarketHours.TelegramAllowed(symbol, "Daily", "A+", AWeekdayNoon));
        Assert.False(MarketHours.TelegramAllowed(symbol, "15m", "B", AWeekdayNoon)); // GBP/JPY's own 15m carve-out too
    }

    [Fact]
    public void OtherSymbolsAreUnaffectedByTheTelegramPause()
    {
        Assert.True(MarketHours.TelegramAllowed("GBP/USD", "1H", "A+", AWeekdayNoon));
        Assert.True(MarketHours.TelegramAllowed("EUR/USD", "15m", "B", AWeekdayNoon));
        Assert.True(MarketHours.TelegramAllowed("BTC/USDT", "1H", "A+", AWeekdayNoon));
    }
}
