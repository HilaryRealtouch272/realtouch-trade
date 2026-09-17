using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class FvgDetectorTests
{
    [Fact]
    public void DetectsABullishGapWhenThirdCandleLowClearsFirstCandleHigh()
    {
        // Build enough history for a stable ATR, then a clean 3-candle bullish FVG.
        var warmup = CandleFixtures.RangingBars(30, mid: 100m, amplitude: 1m);
        var bars = new List<(decimal, decimal, decimal, decimal)>(warmup)
        {
            (100, 101, 99, 100),   // first
            (100, 108, 100, 107),  // middle - big displacement
            (107, 112, 106, 111)   // third - low (106) clears first's high (101)
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var gaps = FvgDetector.Detect(candles, Timeframe.Daily);

        Assert.Contains(gaps, g => g.Direction == FvgDirection.Bullish);
    }

    [Fact]
    public void DoesNotDetectAGapNarrowerThanTheAtrThreshold()
    {
        // Deliberately overlapping candles - third's low/high never clears
        // first's high/low at all, so no triple here can form any gap,
        // regardless of ATR (ambient warmup noise may still produce its own
        // unrelated gaps elsewhere, which is fine and expected).
        var warmup = CandleFixtures.RangingBars(30, mid: 100m, amplitude: 1m);
        var thinTripleStart = warmup.Count;
        var bars = new List<(decimal, decimal, decimal, decimal)>(warmup)
        {
            (100, 100.05m, 99.95m, 100),
            (100, 100.03m, 99.97m, 100.01m),
            (100.01m, 100.04m, 99.98m, 100.02m) // third.Low(99.98) < first.High(100.05): no bullish gap possible
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var middleCandleTime = candles[thinTripleStart + 1].OpenTimeUtc;

        var gaps = FvgDetector.Detect(candles, Timeframe.Daily);

        Assert.DoesNotContain(gaps, g => g.OriginCandleTimeUtc == middleCandleTime);
    }

    [Fact]
    public void MarksAGapFullyMitigatedOncePriceTradesBackThroughIt()
    {
        var warmup = CandleFixtures.RangingBars(30, mid: 100m, amplitude: 1m);
        var bars = new List<(decimal, decimal, decimal, decimal)>(warmup)
        {
            (100, 101, 99, 100),
            (100, 108, 100, 107),
            (107, 112, 106, 111),
            (111, 111, 95, 103) // wicks through the full gap (low 95 <= 101) but closes back inside it (103 >= 101)
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var gaps = FvgDetector.Detect(candles, Timeframe.Daily);
        var bullish = Assert.Single(gaps, g => g.Direction == FvgDirection.Bullish);

        Assert.Equal(MitigationStatus.FullyMitigated, bullish.Status);
    }
}
