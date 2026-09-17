using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class DisplacementTests
{
    [Fact]
    public void FlagsALargeDirectionalCandleAfterQuietRangeAsDisplacement()
    {
        var warmup = CandleFixtures.RangingBars(25, mid: 100m, amplitude: 0.5m);
        var bars = new List<(decimal, decimal, decimal, decimal)>(warmup)
        {
            (100, 112, 99.5m, 111) // body 11, range 12.5 -> body ratio ~0.88, huge vs quiet warmup
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        Assert.True(Displacement.IsDisplacementCandle(candles, candles.Count - 1));
    }

    [Fact]
    public void DoesNotFlagAnOrdinarySimilarSizedCandle()
    {
        var warmup = CandleFixtures.RangingBars(25, mid: 100m, amplitude: 1m);
        var candles = CandleFixtures.FromOhlc(warmup, Timeframe.Daily);

        Assert.False(Displacement.IsDisplacementCandle(candles, candles.Count - 1));
    }

    [Fact]
    public void DoesNotFlagALargeBodiedCandleThatClosesAwayFromItsDirectionalEnd()
    {
        var warmup = CandleFixtures.RangingBars(25, mid: 100m, amplitude: 0.5m);
        var bars = new List<(decimal, decimal, decimal, decimal)>(warmup)
        {
            // Big range and body, but closes back near the middle - not "near the directional end".
            (100, 115, 95, 103)
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        Assert.False(Displacement.IsDisplacementCandle(candles, candles.Count - 1));
    }
}
