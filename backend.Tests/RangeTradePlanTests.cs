using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

// Regression: live Range Boundary Rejection trades had the stop only 0.10 x ATR
// beyond the boundary - a 21-point BTC stop and a half-pip EUR/USD stop - so
// reward-to-risk read 17.5 and 20.9 (inflating the score to A+ 95) and the BTC
// trade was stopped out for -1R within 37 minutes. The stop must sit a real,
// tradeable distance beyond the boundary.
public class RangeTradePlanTests
{
    // A clean 20+ candle range: every bar spans 99.5..102.5 with bodies inside 100..102.
    private static List<NormalizedCandle> RangeCandles(int count = 40)
    {
        var bars = new List<(decimal open, decimal high, decimal low, decimal close)>();
        for (var i = 0; i < count; i++)
            bars.Add(i % 2 == 0 ? (100m, 102.5m, 99.5m, 102m) : (102m, 102.5m, 99.5m, 100m));
        return CandleFixtures.FromOhlc(bars, Timeframe.M15);
    }

    private static decimal Atr(List<NormalizedCandle> candles) =>
        Indicators.Atr(candles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList(), 14)[^1]!.Value;

    [Fact]
    public void TheStopIsAtLeastHalfAnAtrBeyondTheBoundaryNotATenthOfOne()
    {
        var candles = RangeCandles();
        var atr = Atr(candles);

        var plan = RangeTradePlan.Build(candles, Timeframe.M15, SetupDirection.Short, DateTime.UtcNow);

        Assert.NotNull(plan);
        var risk = Math.Abs(plan!.Entry.PreferredEntry - plan.Stop.Price);
        Assert.True(risk >= 0.5m * atr, $"risk {risk} must be at least 0.5 ATR ({0.5m * atr}); the old rule gave {0.1m * atr}");
        Assert.True(plan.Stop.Price > plan.Entry.PreferredEntry, "a Short's stop is above its entry");
    }

    [Fact]
    public void RewardToRiskIsHonestNotAnArtifactOfATinyStop()
    {
        var candles = RangeCandles();

        var plan = RangeTradePlan.Build(candles, Timeframe.M15, SetupDirection.Short, DateTime.UtcNow);

        // Target = equilibrium 1.5 away from a 102.5 boundary; with the old
        // 0.1 ATR stop that read as a large multiple. It must now be modest.
        Assert.NotNull(plan);
        Assert.True(plan!.RewardToRisk < 3m, $"reward-to-risk {plan.RewardToRisk} should not be inflated by a noise-level stop");
    }

    [Fact]
    public void TheInstrumentCostFloorWidensTheStopFurtherWhenCostsWouldEatIt()
    {
        var candles = RangeCandles();
        var atr = Atr(candles);
        var floor = 3m * atr; // costs equal to three ATR: far above half an ATR

        var plan = RangeTradePlan.Build(candles, Timeframe.M15, SetupDirection.Long, DateTime.UtcNow, floor);

        Assert.NotNull(plan);
        Assert.True(Math.Abs(plan!.Entry.PreferredEntry - plan.Stop.Price) >= floor);
        Assert.True(plan.Stop.Price < plan.Entry.PreferredEntry, "a Long's stop is below its entry");
    }

    [Fact]
    public void ACostFloorBeyondTheSanityBoundMeansNoPlanRatherThanAHugeStop()
    {
        var candles = RangeCandles();
        var atr = Atr(candles);

        var plan = RangeTradePlan.Build(candles, Timeframe.M15, SetupDirection.Long, DateTime.UtcNow, minStopCostFloor: 20m * atr);

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("EUR/USD", 0.00052)]  // (1.0 + 0.3) pips x 0.0001 x 4
    [InlineData("BTC/USDT", 32.0)]     // (5 + 3) points x 1 x 4
    [InlineData("XAU/USD", 1.6)]       // (30 + 10) points x 0.01 x 4
    [InlineData("NOT/CONFIGURED", 0.0)] // no metadata: only the ATR minimum applies
    public void TheCostFloorIsFourTimesTheInstrumentsSpreadPlusSlippageInPriceTerms(string symbol, double expected)
    {
        Assert.Equal((decimal)expected, SignalOrchestrator.MinStopCostFloor(symbol));
    }
}
