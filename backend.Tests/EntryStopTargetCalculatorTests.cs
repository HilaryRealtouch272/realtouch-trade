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
    public void NeverPlacesTheStopOnTheWrongSideOfEntryEvenWhenPriceHasDriftedFromTheZone()
    {
        // Regression: a real deployed run once produced a Long with its stop
        // ABOVE entry (and, as a direct symptom, TP1 landing exactly on the
        // stop) because the old candidate filter validated stop candidates
        // against the live close instead of the actual entry price. A
        // matching-direction sweep pivoted well ABOVE the entry zone
        // reproduces that exact scenario - it must never be chosen as the
        // stop regardless of where the current close sits.
        var ctx = BuildUptrendContext();
        var completed = ctx.candles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList();
        var entryZoneOb = ctx.obs.First(o => o.Direction == OrderBlockDirection.Bullish);
        var entryMidpoint = (entryZoneOb.ProximalBoundary + entryZoneOb.DistalBoundary) / 2;

        var badSweep = new LiquiditySweep(
            SweepDirection.Bullish,
            SweptLevel: entryMidpoint + Math.Abs(entryMidpoint) * 0.05m, // above entry - the wrong side for a Long stop
            SweepCandleTimeUtc: completed[^1].CloseTimeUtc,
            Timeframe.Daily,
            SweptPivot: new Pivot(completed.Count - 1, completed[^1].OpenTimeUtc, entryMidpoint, PivotType.Low, completed[^1].CloseTimeUtc));

        var plan = EntryStopTargetCalculator.Compute(
            ctx.candles, Timeframe.Daily, SetupDirection.Long, ctx.structure,
            ctx.obs, ctx.fvgs, ctx.willis, new[] { badSweep }, ctx.levels);

        if (plan is not null)
        {
            Assert.True(plan.Stop.Price < plan.Entry.PreferredEntry);
            Assert.NotEqual(plan.Stop.Price, plan.Targets.Tp1);
        }
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
