using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class ModelRoutingTests
{
    [Theory]
    [InlineData(SetupModelType.TrendContinuationPullback, MarketCondition.TrendingBullish, true)]
    [InlineData(SetupModelType.TrendContinuationPullback, MarketCondition.Ranging, false)]
    [InlineData(SetupModelType.RangeBoundaryRejection, MarketCondition.Ranging, true)]
    [InlineData(SetupModelType.RangeBoundaryRejection, MarketCondition.TrendingBearish, false)]
    [InlineData(SetupModelType.BreakoutAndRetest, MarketCondition.BreakoutBullish, true)]
    [InlineData(SetupModelType.LiquiditySweepReversal, MarketCondition.ReversalDeveloping, true)]
    public void EachModelOnlyQualifiesInItsDocumentedConditions(SetupModelType model, MarketCondition condition, bool expected)
    {
        Assert.Equal(expected, ModelRouting.Supports(model, condition));
    }

    [Fact]
    public void AnUnresolvedConditionSupportsNoModelAtAll()
    {
        foreach (var model in Enum.GetValues<SetupModelType>())
            Assert.False(ModelRouting.Supports(model, MarketCondition.NeutralOrTransition));
    }

    [Fact]
    public void AnUnsupportedConditionFailsTheRoutingGateWithItsReasonCode()
    {
        var failed = SignalOrchestrator.EvaluateRoutingGates(SetupModelType.TrendContinuationPullback, MarketCondition.NeutralOrTransition, HtfAlignment.Aligned);

        Assert.Contains(ReasonCode.MARKET_CONDITION_NOT_SUPPORTED, failed);
    }

    [Fact]
    public void ARangeTradeAgainstAConflictingHigherTimeframeIsBlocked()
    {
        // The GBP/USD case: a long at a range floor while the higher timeframes trend down.
        var failed = SignalOrchestrator.EvaluateRoutingGates(SetupModelType.RangeBoundaryRejection, MarketCondition.Ranging, HtfAlignment.Conflicting);

        Assert.Equal(new[] { ReasonCode.HTF_CONFLICT }, failed);
    }

    [Theory]
    [InlineData(HtfAlignment.Aligned)]
    [InlineData(HtfAlignment.Neutral)]
    public void ARangeTradeWithAlignedOrNeutralHigherTimeframesIsNotBlocked(HtfAlignment htf)
    {
        Assert.Empty(SignalOrchestrator.EvaluateRoutingGates(SetupModelType.RangeBoundaryRejection, MarketCondition.Ranging, htf));
    }

    [Fact]
    public void EqualScoresPreferTheModelNativeToTheCondition()
    {
        StrategyEvaluation Eval(SetupModelType m) => new(m, "v", true, true, 80, "B", 75,
            Array.Empty<ConfluenceFamilyScore>(), Array.Empty<ReasonCode>(), Array.Empty<ReasonCode>(), null);

        var picked = SignalOrchestrator.SelectPrimary(
            new[] { Eval(SetupModelType.LiquiditySweepReversal), Eval(SetupModelType.TrendContinuationPullback) },
            MarketCondition.TrendingBullish);

        Assert.Equal(SetupModelType.TrendContinuationPullback, picked!.StrategyId);
    }
}
