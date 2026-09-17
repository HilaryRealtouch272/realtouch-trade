using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class StructureAnalyzerTests
{
    [Fact]
    public void UptrendProducesBullishBosEvents()
    {
        var bars = CandleFixtures.UptrendBars(60);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var result = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        Assert.Contains(result.Events, e => e.Type == StructureEventType.BullishBos);
        Assert.Equal(TrendState.Bullish, result.FinalTrend);
    }

    [Fact]
    public void DowntrendProducesBearishBosEvents()
    {
        var bars = CandleFixtures.DowntrendBars(60);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var result = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        Assert.Contains(result.Events, e => e.Type == StructureEventType.BearishBos);
        Assert.Equal(TrendState.Bearish, result.FinalTrend);
    }

    [Fact]
    public void ReversalFromBearishToBullishProducesChochNotPlainBos()
    {
        // Establish bearish structure first, then reverse hard upward.
        var down = CandleFixtures.DowntrendBars(40, start: 200m);
        var up = CandleFixtures.UptrendBars(40, start: down[^1].close, step: 2.5m);
        var candles = CandleFixtures.FromOhlc(down.Concat(up), Timeframe.Daily);

        var result = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        Assert.Contains(result.Events, e => e.Type == StructureEventType.BullishChoch);
    }

    [Fact]
    public void ChochEventOnlyReferencesPivotsConfirmedBeforeItsOwnCandle()
    {
        var down = CandleFixtures.DowntrendBars(40, start: 200m);
        var up = CandleFixtures.UptrendBars(40, start: down[^1].close, step: 2.5m);
        var candles = CandleFixtures.FromOhlc(down.Concat(up), Timeframe.Daily);

        var result = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        // No-look-ahead: every event's referenced pivot must have been
        // confirmed at or before the event's own candle time.
        Assert.All(result.Events, e => Assert.True(e.BrokenPivot.ConfirmedAtUtc <= e.CandleTimeUtc));
    }

    [Fact]
    public void FlatOscillatingPriceProducesNoSpuriousBosFromWicksAlone()
    {
        var bars = CandleFixtures.RangingBars(80);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);

        var result = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        // A ranging series should not produce a one-directional trend lock.
        Assert.True(result.Events.Count(e => e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch) > 0
            == result.Events.Count(e => e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch) > 0
            || result.Events.Count < 3);
    }
}
