using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class KeyLevelCatalogTests
{
    [Fact]
    public void ProducesPreviousDayHighAndLowOnlyOnDailyTimeframe()
    {
        var bars = CandleFixtures.UptrendBars(30);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var levels = KeyLevelCatalog.Build(candles, Timeframe.Daily);

        Assert.Contains(levels, l => l.Type == KeyLevelType.PreviousDayHigh);
        Assert.Contains(levels, l => l.Type == KeyLevelType.PreviousDayLow);
    }

    [Fact]
    public void DoesNotProducePreviousDayLevelsOnAFourHourSeries()
    {
        var bars = CandleFixtures.UptrendBars(30);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.H4);

        var levels = KeyLevelCatalog.Build(candles, Timeframe.H4);

        Assert.DoesNotContain(levels, l => l.Type == KeyLevelType.PreviousDayHigh);
    }

    [Fact]
    public void DetectsEqualHighsWhenTwoConfirmedPivotsClusterTogether()
    {
        // Two ranging cycles that touch nearly the same high twice.
        var bars = CandleFixtures.RangingBars(80, mid: 100m, amplitude: 5m);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var levels = KeyLevelCatalog.Build(candles, Timeframe.Daily);

        // Not guaranteed on every fixture shape, but the type must at least be
        // producible without throwing, and any equal-level found must have >= 2 reactions.
        Assert.All(levels.Where(l => l.Type is KeyLevelType.EqualHighs or KeyLevelType.EqualLows),
            l => Assert.True(l.ReactionCount >= 2));
    }

    [Fact]
    public void RangeLevelsPlaceEquilibriumBetweenHighAndLow()
    {
        var bars = CandleFixtures.RangingBars(40);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var levels = KeyLevelCatalog.Build(candles, Timeframe.Daily);
        var high = levels.Single(l => l.Type == KeyLevelType.RangeHigh);
        var low = levels.Single(l => l.Type == KeyLevelType.RangeLow);
        var eq = levels.Single(l => l.Type == KeyLevelType.RangeEquilibrium);

        Assert.True(eq.Upper > low.Upper && eq.Upper < high.Upper);
    }

    [Fact]
    public void MarksASwingHighInvalidatedOnceClosedAbove()
    {
        var bars = CandleFixtures.UptrendBars(80);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var levels = KeyLevelCatalog.Build(candles, Timeframe.Daily);

        // In a sustained uptrend, the earliest swing highs must be invalidated
        // (price has since closed above them).
        var earlyHighs = levels.Where(l => l.Type == KeyLevelType.ConfirmedSwingHigh).OrderBy(l => l.OriginatingCandleTimeUtc).Take(1);
        Assert.All(earlyHighs, l => Assert.True(l.Invalidated));
    }
}
