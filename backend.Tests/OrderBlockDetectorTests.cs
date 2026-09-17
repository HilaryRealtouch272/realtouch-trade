using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class OrderBlockDetectorTests
{
    [Fact]
    public void DetectsABullishOrderBlockAtTheLastBearishCandleBeforeTheBosMove()
    {
        var bars = CandleFixtures.UptrendBars(80);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var blocks = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);

        Assert.Contains(blocks, b => b.Direction == OrderBlockDirection.Bullish);
    }

    [Fact]
    public void ValidOrderBlockIsNotClosedThroughItsDistalBoundary()
    {
        var bars = CandleFixtures.UptrendBars(80);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var blocks = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);
        var valid = blocks.Where(OrderBlockDetector.IsValid).ToList();

        Assert.All(valid, b => Assert.False(b.ClosedThroughDistal));
    }

    [Fact]
    public void BullishOrderBlockProximalIsAboveItsDistal()
    {
        var bars = CandleFixtures.UptrendBars(80);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var blocks = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);

        Assert.All(blocks.Where(b => b.Direction == OrderBlockDirection.Bullish),
            b => Assert.True(b.ProximalBoundary >= b.DistalBoundary));
    }
}
