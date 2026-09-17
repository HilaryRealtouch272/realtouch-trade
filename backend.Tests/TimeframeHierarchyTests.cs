using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class TimeframeHierarchyTests
{
    [Theory]
    [InlineData(Timeframe.Weekly, Timeframe.H4)]
    [InlineData(Timeframe.Daily, Timeframe.M15)]
    [InlineData(Timeframe.H4, Timeframe.M15)]
    [InlineData(Timeframe.H1, Timeframe.M15)]
    [InlineData(Timeframe.M15, Timeframe.M15)]
    public void EntryTimeframeMatchesTheSpecTable(Timeframe displayed, Timeframe expectedEntry)
    {
        Assert.Equal(expectedEntry, TimeframeHierarchy.EntryTimeframe(displayed));
    }

    [Fact]
    public void ContextTimeframesForDailyAreWeeklyAndDaily()
    {
        var context = TimeframeHierarchy.ContextTimeframes(Timeframe.Daily);
        Assert.Equal(new[] { Timeframe.Weekly, Timeframe.Daily }, context);
    }

    [Fact]
    public void AlignedWhenEveryContextTimeframeSupportsTheDirection()
    {
        var context = new Dictionary<Timeframe, MarketCondition>
        {
            [Timeframe.Weekly] = MarketCondition.TrendingBullish,
            [Timeframe.Daily] = MarketCondition.BreakoutBullish
        };

        var result = TimeframeHierarchy.Evaluate(SetupDirection.Long, context);

        Assert.Equal(HtfAlignment.Aligned, result);
    }

    [Fact]
    public void ConflictingWhenAnyContextTimeframeOpposesTheDirection()
    {
        var context = new Dictionary<Timeframe, MarketCondition>
        {
            [Timeframe.Weekly] = MarketCondition.TrendingBearish,
            [Timeframe.Daily] = MarketCondition.TrendingBullish
        };

        var result = TimeframeHierarchy.Evaluate(SetupDirection.Long, context);

        Assert.Equal(HtfAlignment.Conflicting, result);
    }

    [Fact]
    public void NeutralWhenContextHasNoDirectionalOpinion()
    {
        var context = new Dictionary<Timeframe, MarketCondition>
        {
            [Timeframe.Weekly] = MarketCondition.Ranging,
            [Timeframe.Daily] = MarketCondition.NeutralOrTransition
        };

        var result = TimeframeHierarchy.Evaluate(SetupDirection.Long, context);

        Assert.Equal(HtfAlignment.Neutral, result);
    }
}
