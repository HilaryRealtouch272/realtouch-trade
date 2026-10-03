using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
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
    public void AGradeChangeOnTheSameSetupIsNotANewAlert()
    {
        Assert.True(SignalAlertService.SameSetup("Long", "TrendContinuationPullback", "Long", "TrendContinuationPullback"));
        Assert.False(SignalAlertService.SameSetup("Long", "TrendContinuationPullback", "Short", "TrendContinuationPullback"));
        Assert.False(SignalAlertService.SameSetup("Long", "TrendContinuationPullback", "Long", "BreakoutAndRetest"));
    }

    [Theory]
    [InlineData("15m", false)]
    [InlineData("1H", true)]
    [InlineData("Daily", true)]
    public void OutcomeUpdatesForFifteenMinuteTradesAreNotSent(string timeframe, bool send)
    {
        var entry = new QualificationLogEntry("x", "BTC/USDT", timeframe, "Long", "TrendContinuationPullback", "A", 86, 1m, 0.9m, 1.1m, 1.2m, 1.4m, 2m, DateTime.UtcNow, DateTime.UtcNow, "Tp1Hit", null, null, null, null);
        Assert.Equal(send, SignalLogService.ShouldSendOutcome(entry));
    }
}
