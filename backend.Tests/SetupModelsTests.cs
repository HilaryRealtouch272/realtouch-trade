using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class SetupModelsTests
{
    private static (List<NormalizedCandle> candles, StructureResult structure, MarketCondition condition,
        IReadOnlyList<OrderBlock> obs, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<RealtouchWillisZone> willis,
        IReadOnlyList<LiquiditySweep> sweeps) BuildUptrendContext()
    {
        var bars = CandleFixtures.UptrendBars(120);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);
        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);
        var obs = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);
        var fvgs = FvgDetector.Detect(candles, Timeframe.Daily);
        var willis = WillisZoneDetector.Detect(candles, Timeframe.Daily, structure, obs, fvgs);
        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);
        return (candles, structure, condition, obs, fvgs, willis, sweeps);
    }

    [Fact]
    public void TrendContinuationReturnsNullWhenConditionIsNotTrending()
    {
        var ctx = BuildUptrendContext();

        var result = SetupModels.EvaluateTrendContinuationPullback(
            ctx.candles, Timeframe.Daily, MarketCondition.Ranging, ctx.structure, ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps);

        Assert.Null(result);
    }

    [Fact]
    public void TrendContinuationOnATrendingMarketNeverReportsQualifiedYet()
    {
        var ctx = BuildUptrendContext();
        Assert.Equal(MarketCondition.TrendingBullish, ctx.condition); // sanity: fixture actually is trending

        var result = SetupModels.EvaluateTrendContinuationPullback(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps);

        Assert.NotNull(result);
        Assert.Equal(SetupDirection.Long, result!.Direction);
        // Entry/stop/target math isn't implemented yet - nothing should ever claim to be fully qualified.
        Assert.False(result.Qualified);
        Assert.Contains(result.Requirements, r => r.Status == RequirementStatus.NotEvaluated);
    }

    [Fact]
    public void BreakoutAndRetestReturnsNullWhenConditionIsNotBreakout()
    {
        var ctx = BuildUptrendContext();

        var result = SetupModels.EvaluateBreakoutAndRetest(ctx.candles, Timeframe.Daily, MarketCondition.Ranging, ctx.structure, ctx.obs, ctx.fvgs);

        Assert.Null(result);
    }

    [Fact]
    public void LiquiditySweepReversalReturnsNullWhenThereIsNoChoch()
    {
        var ctx = BuildUptrendContext();
        // A clean uptrend's last event is a BOS, not a CHoCH (no prior opposite trend to reverse from).
        var lastEvent = ctx.structure.Events.LastOrDefault();
        Assert.True(lastEvent is null || lastEvent.Type is StructureEventType.BullishBos or StructureEventType.BearishBos);

        var result = SetupModels.EvaluateLiquiditySweepReversal(ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.obs, ctx.fvgs, ctx.sweeps);

        Assert.Null(result);
    }

    [Fact]
    public void RangeBoundaryRejectionReturnsNullWhenConditionIsNotRanging()
    {
        var ctx = BuildUptrendContext();

        var result = SetupModels.EvaluateRangeBoundaryRejection(ctx.candles, Timeframe.Daily, MarketCondition.TrendingBullish, ctx.structure, ctx.sweeps);

        Assert.Null(result);
    }

    [Fact]
    public void SupplyingHtfAlignmentAndRewardToRiskRemovesAllNotEvaluatedRequirements()
    {
        var ctx = BuildUptrendContext();

        var result = SetupModels.EvaluateTrendContinuationPullback(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps,
            htfAlignment: HtfAlignment.Aligned, rewardToRisk: 2.5m, calendarVeto: CalendarVetoState.NoVeto);

        Assert.NotNull(result);
        // Proves the wiring works: once real HTF alignment, R:R, and calendar
        // state are all supplied, nothing is left NotEvaluated - Qualified now
        // depends purely on whether the OTHER checks (location, zone, sweep,
        // structure) are actually Met on this fixture, not on permanently-missing data.
        Assert.True(result!.FullyEvaluated);
    }

    [Fact]
    public void AnActiveHardNewsVetoBlocksQualificationEvenWhenEverythingElseIsSupplied()
    {
        var ctx = BuildUptrendContext();

        var result = SetupModels.EvaluateTrendContinuationPullback(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps,
            htfAlignment: HtfAlignment.Aligned, rewardToRisk: 2.5m, calendarVeto: CalendarVetoState.HardVeto);

        Assert.NotNull(result);
        Assert.False(result!.Qualified);
        Assert.Contains(result.Requirements, r => r.Description.Contains("news/economic-calendar veto") && r.Status == RequirementStatus.NotMet);
    }

    [Fact]
    public void ConflictingHtfAlignmentIsReportedAsNotMetRatherThanBlockingEvaluation()
    {
        var ctx = BuildUptrendContext();

        var result = SetupModels.EvaluateTrendContinuationPullback(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps,
            htfAlignment: HtfAlignment.Conflicting, rewardToRisk: 2.5m);

        Assert.NotNull(result);
        Assert.False(result!.Qualified);
        Assert.Contains(result.Requirements, r => r.Description.Contains("directional alignment") && r.Status == RequirementStatus.NotMet);
    }

    [Fact]
    public void EveryProducedCandidateCarriesAtLeastOneRequirement()
    {
        var ctx = BuildUptrendContext();
        var result = SetupModels.EvaluateTrendContinuationPullback(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Requirements);
        Assert.All(result.Requirements, r => Assert.False(string.IsNullOrWhiteSpace(r.Description)));
    }
}
