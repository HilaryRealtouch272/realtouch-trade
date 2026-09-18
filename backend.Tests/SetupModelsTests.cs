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

    private static (List<NormalizedCandle> candles, StructureResult structure, MarketCondition condition,
        IReadOnlyList<LiquiditySweep> sweeps) BuildReversalContext()
    {
        // A short reversal leg (not a full new trend) so the CHoCH itself
        // stays the LAST structure event - EvaluateLiquiditySweepReversal
        // only fires off the most recent event, not "any CHoCH ever".
        var down = CandleFixtures.DowntrendBars(40, start: 200m);
        var up = CandleFixtures.UptrendBars(8, start: down[^1].close, step: 2.5m);
        var candles = CandleFixtures.FromOhlc(down.Concat(up), Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);
        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);
        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);
        return (candles, structure, condition, sweeps);
    }

    [Fact]
    public void LiquiditySweepReversalReportsControlledRetestMetWhenPriceIsNearTheEvidenceZone()
    {
        var ctx = BuildReversalContext();
        var lastEvent = ctx.structure.Events.LastOrDefault();
        Assert.NotNull(lastEvent);
        Assert.True(lastEvent!.Type is StructureEventType.BullishChoch or StructureEventType.BearishChoch); // sanity: fixture reverses cleanly
        var direction = lastEvent.Type == StructureEventType.BullishChoch ? SetupDirection.Long : SetupDirection.Short;
        var lastClose = ctx.candles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).Last().Close;

        var zoneAtCurrentPrice = new FairValueGap(
            direction == SetupDirection.Long ? FvgDirection.Bullish : FvgDirection.Bearish,
            Upper: lastClose + 0.01m, Lower: lastClose - 0.01m, Midpoint: lastClose,
            OriginCandleTimeUtc: ctx.candles[^1].OpenTimeUtc, Timeframe.Daily, MitigationStatus.Unmitigated);

        var result = SetupModels.EvaluateLiquiditySweepReversal(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure,
            Array.Empty<OrderBlock>(), new[] { zoneAtCurrentPrice }, ctx.sweeps);

        Assert.NotNull(result);
        Assert.Contains(result!.Requirements, r => r.Description == "Entry on the controlled retest" && r.Status == RequirementStatus.Met);
    }

    [Fact]
    public void LiquiditySweepReversalReportsControlledRetestNotMetWhenPriceHasDriftedFarFromAnyZone()
    {
        var ctx = BuildReversalContext();
        var lastEvent = ctx.structure.Events.LastOrDefault();
        Assert.NotNull(lastEvent);
        var direction = lastEvent!.Type == StructureEventType.BullishChoch ? SetupDirection.Long : SetupDirection.Short;
        var lastClose = ctx.candles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).Last().Close;

        var distantZone = new FairValueGap(
            direction == SetupDirection.Long ? FvgDirection.Bullish : FvgDirection.Bearish,
            Upper: lastClose - 100m + 0.01m, Lower: lastClose - 100m - 0.01m, Midpoint: lastClose - 100m,
            OriginCandleTimeUtc: ctx.candles[0].OpenTimeUtc, Timeframe.Daily, MitigationStatus.Unmitigated);

        var result = SetupModels.EvaluateLiquiditySweepReversal(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure,
            Array.Empty<OrderBlock>(), new[] { distantZone }, ctx.sweeps);

        Assert.NotNull(result);
        Assert.Contains(result!.Requirements, r => r.Description == "Entry on the controlled retest" && r.Status == RequirementStatus.NotMet);
    }

    [Fact]
    public void LiquiditySweepReversalRiskAdjustmentReflectsWhetherHtfAlignmentWasSupplied()
    {
        var ctx = BuildReversalContext();

        var withoutHtf = SetupModels.EvaluateLiquiditySweepReversal(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, Array.Empty<OrderBlock>(), Array.Empty<FairValueGap>(), ctx.sweeps);
        var withHtf = SetupModels.EvaluateLiquiditySweepReversal(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, Array.Empty<OrderBlock>(), Array.Empty<FairValueGap>(), ctx.sweeps,
            htfAlignment: HtfAlignment.Conflicting);

        Assert.NotNull(withoutHtf);
        Assert.NotNull(withHtf);
        Assert.Contains(withoutHtf!.Requirements, r => r.Description.Contains("Reduced risk") && r.Status == RequirementStatus.NotEvaluated);
        Assert.Contains(withHtf!.Requirements, r => r.Description.Contains("Reduced risk") && r.Status == RequirementStatus.Met);
    }

    private static (List<NormalizedCandle> candles, StructureResult structure, MarketCondition condition,
        IReadOnlyList<LiquiditySweep> sweeps) BuildRangingContext()
    {
        // 100 bars on this fixture's 12-bar sine cycle lands the very last
        // candle exactly on a peak (index 99 = 90 degrees of phase), so the
        // model's own "near a range boundary" precondition is reliably met.
        var bars = CandleFixtures.RangingBars(100);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);
        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);
        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);
        return (candles, structure, condition, sweeps);
    }

    [Fact]
    public void RangeBoundaryRejectionReportsLogicalTargetMetWhenTargetIsAtEquilibrium()
    {
        var ctx = BuildRangingContext();
        Assert.Equal(MarketCondition.Ranging, ctx.condition); // sanity: fixture actually classifies as ranging
        var completed = ctx.candles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList();
        var window = completed.Skip(Math.Max(0, completed.Count - 20)).ToList();
        var equilibrium = (window.Max(c => c.High) + window.Min(c => c.Low)) / 2;

        var result = SetupModels.EvaluateRangeBoundaryRejection(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.sweeps, targetPrice: equilibrium);

        Assert.NotNull(result);
        Assert.Contains(result!.Requirements, r => r.Description == "Logical target at equilibrium or opposite boundary" && r.Status == RequirementStatus.Met);
    }

    [Fact]
    public void RangeBoundaryRejectionReportsLogicalTargetNotMetWhenTargetIsAnArbitraryExtension()
    {
        var ctx = BuildRangingContext();
        var completed = ctx.candles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList();
        var lastClose = completed[^1].Close;

        var result = SetupModels.EvaluateRangeBoundaryRejection(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.sweeps, targetPrice: lastClose + 500m);

        Assert.NotNull(result);
        Assert.Contains(result!.Requirements, r => r.Description == "Logical target at equilibrium or opposite boundary" && r.Status == RequirementStatus.NotMet);
    }

    [Fact]
    public void RangeBoundaryRejectionLogicalTargetIsNotEvaluatedWithoutARealTarget()
    {
        var ctx = BuildRangingContext();

        var result = SetupModels.EvaluateRangeBoundaryRejection(
            ctx.candles, Timeframe.Daily, ctx.condition, ctx.structure, ctx.sweeps);

        Assert.NotNull(result);
        Assert.Contains(result!.Requirements, r => r.Description == "Logical target at equilibrium or opposite boundary" && r.Status == RequirementStatus.NotEvaluated);
    }
}
