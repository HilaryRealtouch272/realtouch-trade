using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class HigherTimeframeTargetTests
{
    private static KeyLevel Level(Timeframe tf, KeyLevelType type, decimal price) =>
        new(type, tf, price, price, DateTime.UtcNow, DateTime.UtcNow, false, false, 0);

    [Fact]
    public void ANearbyDailyBarrierBecomesTp2AndNamesItsTimeframeAndType()
    {
        // Long: entry 100, stop 95 (risk 5) -> TP1 105. A Daily previous-day high at 108 sits
        // between 1R and 2R, so it caps TP2 at 108 (1.6R), which the R:R gate then judges.
        var levels = new[]
        {
            Level(Timeframe.M15, KeyLevelType.ConfirmedSwingHigh, 130m),
            Level(Timeframe.Daily, KeyLevelType.PreviousDayHigh, 108m)
        };

        var plan = EntryStopTargetCalculator.BuildTargetPlan(100m, 95m, true, levels, atr: 2m);

        Assert.Equal(108m, plan.Tp2);
        Assert.Contains("Daily", plan.Tp2Basis);
        Assert.Contains("PreviousDayHigh", plan.Tp2Basis);
        Assert.Equal(130m, plan.Tp3);
        Assert.Contains("external liquidity level", plan.Tp3Basis);
    }

    [Fact]
    public void WithNoHigherTimeframeLevelTheTargetsFallBackToProjectionsAndSaySo()
    {
        var plan = EntryStopTargetCalculator.BuildTargetPlan(100m, 95m, true, Array.Empty<KeyLevel>(), atr: 2m);

        Assert.Equal(110m, plan.Tp2);
        Assert.Contains("2R projection", plan.Tp2Basis);
        Assert.True(plan.Tp3 >= plan.Tp2 + 5m);
    }

    [Fact]
    public void AShortUsesTheNearestLevelBelowAndOrdersTargetsDownward()
    {
        var levels = new[] { Level(Timeframe.H4, KeyLevelType.ConfirmedSwingLow, 90m), Level(Timeframe.Daily, KeyLevelType.PreviousDayLow, 80m) };

        var plan = EntryStopTargetCalculator.BuildTargetPlan(100m, 105m, false, levels, atr: 2m);

        Assert.Equal(90m, plan.Tp2);
        Assert.Contains("4H", plan.Tp2Basis);
        Assert.Equal(80m, plan.Tp3);
        Assert.True(plan.Tp1 > plan.Tp2 && plan.Tp2 > plan.Tp3);
    }
}
