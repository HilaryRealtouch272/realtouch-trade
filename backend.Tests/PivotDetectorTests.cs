using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class PivotDetectorTests
{
    [Fact]
    public void DetectsAConfirmedHighAtTheCorrectIndex()
    {
        // Daily requires 3 bars each side. Index 5 is the clear local high.
        var bars = new (decimal, decimal, decimal, decimal)[]
        {
            (100, 101, 99, 100), (100, 102, 99, 101), (101, 103, 100, 102),
            (102, 104, 101, 103), (103, 105, 102, 104), (104, 110, 103, 109),
            (109, 109, 106, 107), (107, 107, 104, 105), (105, 106, 103, 104),
            (104, 105, 102, 103), (103, 104, 101, 102)
        };
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var pivots = PivotDetector.DetectConfirmedPivots(candles, Timeframe.Daily);

        var highPivot = Assert.Single(pivots, p => p.Type == PivotType.High);
        Assert.Equal(5, highPivot.Index);
        Assert.Equal(110m, highPivot.Price);
    }

    [Fact]
    public void PivotIsNotConfirmedUntilRequiredRightSideCandlesHaveClosed()
    {
        var bars = CandleFixtures.UptrendBars(20);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var pivots = PivotDetector.DetectConfirmedPivots(candles, Timeframe.Daily);

        foreach (var pivot in pivots)
        {
            // ConfirmedAtUtc must equal the close time of the candle exactly
            // `bars` positions after the pivot candle - never earlier.
            var expectedConfirmIndex = pivot.Index + TimeframeConfig.PivotBars(Timeframe.Daily);
            var expectedConfirmTime = candles[expectedConfirmIndex].CloseTimeUtc;
            Assert.Equal(expectedConfirmTime, pivot.ConfirmedAtUtc);
        }
    }

    [Fact]
    public void DoesNotDetectPivotsWithinUnconfirmableEdgeRegion()
    {
        var bars = CandleFixtures.UptrendBars(10);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var bars_ = TimeframeConfig.PivotBars(Timeframe.Daily);

        var pivots = PivotDetector.DetectConfirmedPivots(candles, Timeframe.Daily);

        Assert.All(pivots, p => Assert.InRange(p.Index, bars_, candles.Count - 1 - bars_));
    }
}
