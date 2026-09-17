using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class MarketConditionClassifierTests
{
    [Fact]
    public void SustainedUptrendClassifiesAsTrendingBullish()
    {
        var bars = CandleFixtures.UptrendBars(100);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);

        Assert.Equal(MarketCondition.TrendingBullish, condition);
    }

    [Fact]
    public void SustainedDowntrendClassifiesAsTrendingBearish()
    {
        var bars = CandleFixtures.DowntrendBars(100);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);

        Assert.Equal(MarketCondition.TrendingBearish, condition);
    }

    [Fact]
    public void OscillatingPriceDoesNotClassifyAsATrend()
    {
        var bars = CandleFixtures.RangingBars(100);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);

        Assert.True(condition is MarketCondition.Ranging or MarketCondition.NeutralOrTransition);
    }

    [Fact]
    public void InsufficientHistoryClassifiesAsNeutralRatherThanGuessing()
    {
        var bars = CandleFixtures.UptrendBars(10);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);

        Assert.Equal(MarketCondition.NeutralOrTransition, condition);
    }
}
