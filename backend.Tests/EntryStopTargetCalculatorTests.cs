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

    [Theory]
    // The real CAKE/USDT 4H case: Long, price 2.58, zone 1.94-1.98, ATR ~0.05
    // so the 3-ATR reach is 0.15 - a 24% pullback is nowhere near reachable.
    [InlineData(true, 2.58, 1.94, 1.98, 0.15, false)]
    [InlineData(true, 2.58, 2.40, 2.46, 0.15, true)]   // within reach: 0.12 above the zone top
    [InlineData(true, 2.58, 2.44, 2.50, 0.15, true)]   // closer still
    [InlineData(true, 2.45, 2.40, 2.50, 0.15, true)]   // price already inside the zone
    [InlineData(true, 2.30, 2.40, 2.50, 0.15, true)]   // price already below a Long zone: left to invalidation rules
    [InlineData(false, 2.00, 2.40, 2.50, 0.15, false)] // Short mirrored: zone 0.40 above price
    [InlineData(false, 2.30, 2.40, 2.50, 0.15, true)]  // Short: zone 0.10 above price
    [InlineData(false, 2.45, 2.40, 2.50, 0.15, true)]  // Short: price inside the zone
    public void AZoneIsOnlyARealisticEntryIfPriceCanReachItWithinTheEntryWindow(
        bool isLong, double lastClose, double zoneMin, double zoneMax, double maxDistance, bool expected)
    {
        Assert.Equal(expected, EntryStopTargetCalculator.IsReachable(
            isLong, (decimal)lastClose, (decimal)zoneMin, (decimal)zoneMax, (decimal)maxDistance));
    }

    private static KeyLevel Level(decimal price) => new(
        KeyLevelType.ConfirmedSwingHigh, Timeframe.H4, price, price, DateTime.UtcNow, DateTime.UtcNow, false, false, 1);

    [Fact]
    public void Tp3IsAlwaysBeyondTp2EvenWhenTp2ComesFromAFarKeyLevelAndNothingLiesPastIt()
    {
        // Regression: Long, entry 100, stop 99 (1R). The nearest opposing key
        // level is at 106 (6R), so TP2 = 106. With nothing beyond it, TP3 used
        // to fall back to a flat 3R = 103 - BELOW TP2. It must be at least 1R
        // past TP2.
        var plan = EntryStopTargetCalculator.BuildTargetPlan(100m, 99m, isLong: true, new[] { Level(106m) }, atr: 1m);

        Assert.Equal(106m, plan.Tp2);
        Assert.True(plan.Tp3 > plan.Tp2, $"TP3 {plan.Tp3} must be beyond TP2 {plan.Tp2}");
        Assert.Equal(107m, plan.Tp3);
    }

    [Fact]
    public void Tp3IsAlwaysBeyondTp2ForAShortToo()
    {
        var plan = EntryStopTargetCalculator.BuildTargetPlan(100m, 101m, isLong: false, new[] { Level(94m) }, atr: 1m);

        Assert.Equal(94m, plan.Tp2);
        Assert.True(plan.Tp3 < plan.Tp2);
        Assert.Equal(93m, plan.Tp3);
    }

    [Fact]
    public void WithNoKeyLevelsTheTargetsKeepTheirPlainRMultiples()
    {
        var plan = EntryStopTargetCalculator.BuildTargetPlan(100m, 99m, isLong: true, Array.Empty<KeyLevel>(), atr: 1m);

        Assert.Equal(101m, plan.Tp1);
        Assert.Equal(102m, plan.Tp2);
        Assert.Equal(103m, plan.Tp3);
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
    public void WidensAnIrrationallyTightStopToTheMinimumTradeableDistanceInsteadOfPlanningOnNoise()
    {
        // A degenerate zone (min == max) forces a near-zero stop distance once
        // the ATR buffer is applied on top of an already-tiny structural level.
        // Such a stop is inside candle noise, so it is widened to the minimum
        // tradeable distance (0.5 ATR) rather than planned as-is - or thrown
        // away, which would lose the setup.
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

        Assert.NotNull(plan);
        var atr = Indicators.Atr(completed, 14)[^1]!.Value;
        var risk = Math.Abs(plan!.Entry.PreferredEntry - plan.Stop.Price);
        Assert.True(risk >= 0.5m * atr, $"stop distance {risk} must be at least 0.5 ATR ({0.5m * atr})");
        Assert.True(plan.Stop.Price < plan.Entry.PreferredEntry, "a Long stop stays below entry");
        Assert.Contains("widened", plan.Stop.Reason);
    }

    [Theory]
    // isLong, entry, stop, atr, costFloor -> expected stop, expected rational
    [InlineData(true, 100.0, 99.9, 2.0, 0.0, 99.0, true)]    // 0.1 tight: widened to 0.5 ATR = 1.0
    [InlineData(true, 100.0, 99.9, 2.0, 3.0, 97.0, true)]    // cost floor (3.0) beats 0.5 ATR
    [InlineData(true, 100.0, 97.0, 2.0, 0.0, 97.0, true)]    // already far enough: untouched
    [InlineData(false, 100.0, 100.1, 2.0, 0.0, 101.0, true)] // Short mirror
    [InlineData(false, 100.0, 100.1, 2.0, 3.0, 103.0, true)]
    [InlineData(true, 100.0, 99.9, 2.0, 17.0, 83.0, false)]  // costs alone exceed 8 ATR: untradeable, no plan
    public void ATooTightStopIsWidenedToTheLargerOfHalfAnAtrAndTheCostFloor(
        bool isLong, double entry, double stop, double atr, double costFloor, double expectedStop, bool expectedRational)
    {
        var result = EntryStopTargetCalculator.EnforceMinimumStop(
            new StopPlan((decimal)stop, "structure", IsRational: true), (decimal)entry, isLong, (decimal)atr, (decimal)costFloor);

        Assert.Equal((decimal)expectedStop, result.Price);
        Assert.Equal(expectedRational, result.IsRational);
    }
}
