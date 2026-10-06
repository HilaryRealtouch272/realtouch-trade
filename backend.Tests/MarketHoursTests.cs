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
}
