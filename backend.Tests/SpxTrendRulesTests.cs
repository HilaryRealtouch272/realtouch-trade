using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Tests;

public class SpxTrendRulesTests
{
    [Theory]
    [InlineData(2026, 7, 15, 20)]
    [InlineData(2026, 11, 19, 21)]
    public void CloseCutoffTracksNewYorkDst(int year, int month, int day, int hour)
    {
        Assert.Equal(new DateTime(year, month, day, hour, 15, 0, DateTimeKind.Utc),
            SpxTrendRules.SessionClose(new DateTime(year, month, day)));
    }

    [Fact]
    public void TrendExitNeedsClosedBarAndFiftyPriorBarsAndHasNoTimeExpiry()
    {
        var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 61).Select(i => new NormalizedCandle(SpxTrendRules.Symbol,
            "^GSPC", "Yahoo", Timeframe.Daily, day.AddDays(i), day.AddDays(i).AddHours(21),
            100m, 102m, 99m, 100m, null, true, day, DataQuality.Ok)).ToList();
        var pos = new VtbPosition(VtbSide.Long, day, 100m, 20m, 80m, 0m);
        Assert.Null(SpxTrendRules.Settle(pos, bars, day.AddDays(70)));
        bars[^1] = bars[^1] with { Low = 97m, Close = 98m, IsComplete = false };
        Assert.Null(SpxTrendRules.Settle(pos, bars, day.AddDays(60).AddHours(20)));
        bars[^1] = bars[^1] with { IsComplete = true };
        var trade = SpxTrendRules.Settle(pos, bars, day.AddDays(61));
        Assert.Equal(VtbExitReason.Channel, trade!.Reason);
        Assert.Equal(98m, trade.Exit);
        bars[^1] = bars[^1] with { Open = 75m, Low = 74m };
        Assert.Equal(75m, SpxTrendRules.Settle(pos, bars, day.AddDays(61))!.Exit);
    }
}
