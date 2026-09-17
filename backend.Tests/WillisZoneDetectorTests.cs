using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class WillisZoneDetectorTests
{
    [Fact]
    public void EveryDetectedZoneOverlapsAnFvgOfTheSameDirection()
    {
        var bars = CandleFixtures.UptrendBars(120);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);
        var orderBlocks = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);
        var fvgs = FvgDetector.Detect(candles, Timeframe.Daily);

        var zones = WillisZoneDetector.Detect(candles, Timeframe.Daily, structure, orderBlocks, fvgs);

        Assert.All(zones, z => Assert.NotNull(z.OverlappingFvg));
        Assert.All(zones, z => Assert.True(z.OverlapsToleratedRetracement));
    }

    [Fact]
    public void NoZonesAreProducedWhenThereAreNoFvgsToOverlap()
    {
        var bars = CandleFixtures.UptrendBars(80);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);
        var orderBlocks = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);

        var zones = WillisZoneDetector.Detect(candles, Timeframe.Daily, structure, orderBlocks, Array.Empty<FairValueGap>());

        Assert.Empty(zones);
    }
}
