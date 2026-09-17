using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class LiquiditySweepDetectorTests
{
    [Fact]
    public void DetectsABullishSweepWhenPriceWicksBelowAConfirmedLowThenClosesBackAboveWithFollowThrough()
    {
        // Uptrend establishes confirmed swing lows, then a deliberate wick
        // below the most recent one, closing back above it, followed by a
        // strong bullish displacement candle.
        var up = CandleFixtures.UptrendBars(60);
        var lastLow = up[^1].close - 2m;
        var bars = new List<(decimal, decimal, decimal, decimal)>(up)
        {
            (up[^1].close, up[^1].close, lastLow - 3m, lastLow + 0.5m), // wicks below, closes back above
            (lastLow + 0.5m, lastLow + 15m, lastLow, lastLow + 14m)     // strong bullish displacement follow-through
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);

        Assert.Contains(sweeps, s => s.Direction == SweepDirection.Bullish);
    }

    [Fact]
    public void DoesNotDetectABullishSweepOfTheMostRecentLowWhenTheCandleFailsToCloseBackAboveIt()
    {
        var up = CandleFixtures.UptrendBars(60);
        var mostRecentLowPivot = PivotDetector.DetectConfirmedPivots(CandleFixtures.FromOhlc(up, Timeframe.Daily), Timeframe.Daily)
            .Where(p => p.Type == PivotType.Low).OrderByDescending(p => p.CandleTimeUtc).First();
        var bars = new List<(decimal, decimal, decimal, decimal)>(up)
        {
            // Wicks below the most recent confirmed low AND closes below it too - never reclaims it.
            (up[^1].close, up[^1].close, mostRecentLowPivot.Price - 10m, mostRecentLowPivot.Price - 5m)
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);

        // A bearish sweep of an unrelated earlier swing high from this same
        // trending fixture is fine and expected - only the bullish reclaim
        // of *this* low must never be reported, since price never closed
        // back above it.
        Assert.DoesNotContain(sweeps, s => s.Direction == SweepDirection.Bullish && s.SweptPivot == mostRecentLowPivot);
    }

    [Fact]
    public void DoesNotDetectASweepWithoutFollowThroughDisplacementOrStructureEvent()
    {
        var up = CandleFixtures.UptrendBars(60);
        var lastLow = up[^1].close - 2m;
        var bars = new List<(decimal, decimal, decimal, decimal)>(up)
        {
            (up[^1].close, up[^1].close, lastLow - 3m, lastLow + 0.5m), // wicks below, closes back above
        };
        // Follow with flat/indecisive candles - no displacement, no BOS/CHoCH.
        for (int i = 0; i < 5; i++)
            bars.Add((lastLow + 0.5m, lastLow + 0.6m, lastLow + 0.4m, lastLow + 0.5m));

        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);

        Assert.Empty(sweeps);
    }
}
