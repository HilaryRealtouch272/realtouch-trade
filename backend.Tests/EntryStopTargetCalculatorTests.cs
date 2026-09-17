using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class EntryStopTargetCalculatorTests
{
    private static (List<NormalizedCandle> candles, StructureResult structure, IReadOnlyList<OrderBlock> obs,
        IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<RealtouchWillisZone> willis, IReadOnlyList<LiquiditySweep> sweeps,
        IReadOnlyList<KeyLevel> levels) BuildUptrendContext()
    {
        var bars = CandleFixtures.UptrendBars(120);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);
        var obs = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);
        var fvgs = FvgDetector.Detect(candles, Timeframe.Daily);
        var willis = WillisZoneDetector.Detect(candles, Timeframe.Daily, structure, obs, fvgs);
        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);
        var levels = KeyLevelCatalog.Build(candles, Timeframe.Daily);
        return (candles, structure, obs, fvgs, willis, sweeps, levels);
    }

    [Fact]
    public void ReturnsNullWhenNoQualifyingZoneExists()
    {
        var ctx = BuildUptrendContext();

        var plan = EntryStopTargetCalculator.Compute(
            ctx.candles, Timeframe.Daily, SetupDirection.Long, ctx.structure,
            Array.Empty<OrderBlock>(), Array.Empty<FairValueGap>(), Array.Empty<RealtouchWillisZone>(),
            ctx.sweeps, ctx.levels);

        Assert.Null(plan);
    }

    [Fact]
    public void BullishPlanPlacesStopBelowEntryAndTargetsAboveEntry()
    {
        var ctx = BuildUptrendContext();

        var plan = EntryStopTargetCalculator.Compute(
            ctx.candles, Timeframe.Daily, SetupDirection.Long, ctx.structure,
            ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps, ctx.levels);

        Assert.NotNull(plan);
        Assert.True(plan!.Stop.Price < plan.Entry.PreferredEntry);
        Assert.True(plan.Targets.Tp1 > plan.Entry.PreferredEntry);
        Assert.True(plan.Targets.Tp2 >= plan.Targets.Tp1);
        Assert.True(plan.Targets.Tp3 >= plan.Targets.Tp2);
    }

    [Fact]
    public void TargetWeightsSumToOne()
    {
        var ctx = BuildUptrendContext();
        var plan = EntryStopTargetCalculator.Compute(
            ctx.candles, Timeframe.Daily, SetupDirection.Long, ctx.structure,
            ctx.obs, ctx.fvgs, ctx.willis, ctx.sweeps, ctx.levels);

        Assert.NotNull(plan);
        Assert.Equal(1m, plan!.Targets.Tp1Weight + plan.Targets.Tp2Weight + plan.Targets.Tp3Weight);
    }

    [Fact]
    public void RejectsAnIrrationallyTightStopDistance()
    {
        // A degenerate zone (min == max) forces a near-zero stop distance once
        // the ATR buffer is applied on top of an already-tiny structural level.
        var ctx = BuildUptrendContext();
        var completed = ctx.candles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList();
        var tinyBlock = new OrderBlock(
            OrderBlockDirection.Bullish,
            ProximalBoundary: completed[^1].Close + 0.0001m,
            DistalBoundary: completed[^1].Close,
            CandleTimeUtc: completed[^1].OpenTimeUtc,
            Timeframe.Daily,
            ctx.structure.Events.Last(),
            ClosedThroughDistal: false,
            ReactionCount: 0
        );

        var plan = EntryStopTargetCalculator.Compute(
            ctx.candles, Timeframe.Daily, SetupDirection.Long, ctx.structure,
            new[] { tinyBlock }, Array.Empty<FairValueGap>(), Array.Empty<RealtouchWillisZone>(),
            ctx.sweeps, ctx.levels);

        Assert.Null(plan);
    }
}
