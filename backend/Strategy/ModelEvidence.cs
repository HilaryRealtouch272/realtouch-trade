using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Section 6's weight tables need graded quality (breakout close strength,
// sweep quality, range quality), not just the Met/NotMet gates SetupModels.cs
// already checks. This computes that grading from the SAME detector outputs
// (structure, sweeps, order blocks, FVGs, key levels) the orchestrator
// already has, once per model per scan - no new raw-data detection, just
// quality scoring layered on evidence that already exists.
public record BreakoutEvidence(
    bool BreakoutEventFound,
    DateTime? BreakoutCandleTimeUtc,
    decimal BreakoutBodyRatio,      // 0..1: how directional/clean the breaking candle's close was
    decimal BreakoutAtrMultiple,    // distance beyond the broken pivot, in ATRs
    bool RetestReached,
    bool ContinuationConfirmed,     // a real structure event (BOS/CHoCH) after the retest, in direction
    bool ClearSpaceToOpposingLiquidity
);

public record ReversalEvidence(
    bool ChochFound,
    DateTime? ChochCandleTimeUtc,
    bool ReversalLocationSignificant, // the swept pivot sits at a real prior key level, not an arbitrary swing
    bool SweepConfirmed,
    decimal SweepQuality,             // 0..1: how far beyond the pivot the sweep traded, ATR-scaled
    bool CloseBackThroughSweptLevel,
    bool ControlledRetestReached
);

public record RangeEvidence(
    bool RangeEstablished,
    decimal RangeQualityRatio,        // 0..1: how many of the lookback window's closes actually respected the range
    bool NearBoundaryNotEquilibrium,
    bool BoundarySweepOrRejection,
    bool LowerTimeframeConfirmation,
    bool SpaceToEquilibriumOrOppositeBoundary
);

public static class ModelEvidenceBuilder
{
    // How far back to search for a defining event, independent of the
    // Market Condition classifier's own narrow "recent" window (3 candles) -
    // real audit finding: by the time a breakout retest or a post-CHoCH
    // reversal entry actually develops, the classifier has often already
    // relabeled the current condition as Trending/Ranging, and detection
    // gated on the CURRENT single-candle condition snapshot silently missed
    // setups that were still genuinely valid. This is used for DETECTION
    // (did a qualifying event happen recently enough to still be tradeable),
    // never for re-deciding what the CURRENT bar's condition is.
    private const int DetectionLookbackCandles = 30;

    private const decimal MinBodyRatioForBreakout = 0.60m;
    private const decimal BreakoutAtrMultiplier = 0.15m;

    public static BreakoutEvidence BuildBreakout(
        IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, StructureResult structure,
        IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs, SetupDirection direction)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok).OrderBy(c => c.OpenTimeUtc).ToList();
        if (completed.Count < 20) return new(false, null, 0, 0, false, false, false);

        var atr = Indicators.Atr(completed, 14)[^1];
        var lookbackStart = completed[Math.Max(0, completed.Count - DetectionLookbackCandles)].OpenTimeUtc;

        var isLong = direction == SetupDirection.Long;
        var breakEvent = structure.Events
            .Where(e => e.CandleTimeUtc >= lookbackStart)
            .Where(e => isLong ? e.Type is StructureEventType.BullishBos : e.Type is StructureEventType.BearishBos)
            .OrderByDescending(e => e.CandleTimeUtc)
            .FirstOrDefault();
        if (breakEvent is null || atr is null) return new(false, null, 0, 0, false, false, false);

        var breakCandle = completed.FirstOrDefault(c => c.CloseTimeUtc == breakEvent.CandleTimeUtc);
        if (breakCandle is null) return new(false, breakEvent.CandleTimeUtc, 0, 0, false, false, false);

        var range = breakCandle.High - breakCandle.Low;
        var bodyRatio = range == 0 ? 0 : Math.Abs(breakCandle.Close - breakCandle.Open) / range;
        var atrMultiple = atr.Value == 0 ? 0 : Math.Abs(breakEvent.ClosePrice - breakEvent.BrokenPivot.Price) / atr.Value;

        var lastClose = completed[^1].Close;
        var retestTolerance = atr.Value * 0.5m;
        var retestReached =
            Math.Abs(lastClose - breakEvent.BrokenPivot.Price) <= retestTolerance ||
            NearAnyZone(lastClose, orderBlocks, fvgs, direction, retestTolerance);

        var continuationEvent = structure.Events
            .Where(e => e.CandleTimeUtc > breakEvent.CandleTimeUtc)
            .Any(e => isLong ? e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch
                             : e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch);

        // "Clear space to opposing liquidity" - honestly approximated: the
        // breakout distance already covered is itself real, tradeable space,
        // not blocked by the level it just broke through.
        var clearSpace = atrMultiple >= BreakoutAtrMultiplier;

        return new(true, breakEvent.CandleTimeUtc, bodyRatio, atrMultiple, retestReached, continuationEvent, clearSpace);
    }

    public static ReversalEvidence BuildReversal(
        IReadOnlyList<NormalizedCandle> allCandles, StructureResult structure, IReadOnlyList<LiquiditySweep> sweeps,
        IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<KeyLevel> keyLevels, SetupDirection direction)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok).OrderBy(c => c.OpenTimeUtc).ToList();
        if (completed.Count < 20) return new(false, null, false, false, 0, false, false);

        var lookbackStart = completed[Math.Max(0, completed.Count - DetectionLookbackCandles)].OpenTimeUtc;
        var isLong = direction == SetupDirection.Long;
        var chochEvent = structure.Events
            .Where(e => e.CandleTimeUtc >= lookbackStart)
            .Where(e => isLong ? e.Type == StructureEventType.BullishChoch : e.Type == StructureEventType.BearishChoch)
            .OrderByDescending(e => e.CandleTimeUtc)
            .FirstOrDefault();
        if (chochEvent is null) return new(false, null, false, false, 0, false, false);

        var matchingSweep = sweeps
            .Where(s => (s.Direction == SweepDirection.Bullish) == isLong && s.SweepCandleTimeUtc <= chochEvent.CandleTimeUtc)
            .OrderByDescending(s => s.SweepCandleTimeUtc)
            .FirstOrDefault();
        var sweepConfirmed = matchingSweep is not null;

        var atr = Indicators.Atr(completed, 14)[^1];
        var sweepQuality = 0m;
        if (matchingSweep is not null && atr is not null && atr.Value > 0)
        {
            var distance = Math.Abs(matchingSweep.SweptLevel - matchingSweep.SweptPivot.Price);
            sweepQuality = Math.Min(1m, distance / (atr.Value * 0.5m));
        }

        // A close back through the swept level, on or after the sweep candle,
        // is what turns a mere wick into a confirmed reversal rather than an
        // ongoing breakout in the sweep's own direction.
        var closeBackThrough = matchingSweep is not null && completed
            .Where(c => c.OpenTimeUtc >= matchingSweep.SweepCandleTimeUtc)
            .Any(c => isLong ? c.Close > matchingSweep.SweptPivot.Price : c.Close < matchingSweep.SweptPivot.Price);

        // Reversal location significance - honestly approximated: the swept
        // pivot coincides with (or sits inside) a real catalogued key level,
        // not an arbitrary short-term swing. This is the closest available
        // proxy without a dedicated multi-timeframe key-level lookup.
        var reversalLocationSignificant = matchingSweep is not null && keyLevels.Any(l => !l.Invalidated &&
            matchingSweep.SweptLevel >= l.Lower - (atr ?? 0) * 0.25m && matchingSweep.SweptLevel <= l.Upper + (atr ?? 0) * 0.25m);

        var lastClose = completed[^1].Close;
        var retestTolerance = atr is null ? decimal.MaxValue : atr.Value * 0.5m;
        var controlledRetest = NearAnyZone(lastClose, orderBlocks, fvgs, direction, retestTolerance);

        return new(true, chochEvent.CandleTimeUtc, reversalLocationSignificant, sweepConfirmed, sweepQuality, closeBackThrough, controlledRetest);
    }

    public static RangeEvidence BuildRange(
        IReadOnlyList<NormalizedCandle> allCandles, IReadOnlyList<LiquiditySweep> sweeps, StructureResult structure, SetupDirection direction)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok).OrderBy(c => c.OpenTimeUtc).ToList();
        const int rangeLookback = 20;
        if (completed.Count < rangeLookback) return new(false, 0, false, false, false, false);

        var window = completed.Skip(completed.Count - rangeLookback).ToList();
        var rangeHigh = window.Max(c => c.High);
        var rangeLow = window.Min(c => c.Low);
        var rangeWidth = rangeHigh - rangeLow;
        var equilibrium = (rangeHigh + rangeLow) / 2;
        var lastClose = completed[^1].Close;
        var atr = Indicators.Atr(completed, 14)[^1];

        // Range quality: the fraction of the lookback window's closes that
        // actually stayed inside the boundary (honest, deterministic,
        // rewards a real contained range over one dominated by a single
        // wide excursion that happens to define the same high/low).
        var containedCloses = rangeWidth == 0 ? 0 : window.Count(c => c.Close >= rangeLow && c.Close <= rangeHigh);
        var rangeQuality = window.Count == 0 ? 0 : (decimal)containedCloses / window.Count;

        var nearHigh = atr is not null && Math.Abs(lastClose - rangeHigh) <= atr.Value * 0.5m;
        var nearLow = atr is not null && Math.Abs(lastClose - rangeLow) <= atr.Value * 0.5m;
        var nearBoundary = direction == SetupDirection.Short ? nearHigh : nearLow;

        var sweepOrRejection = sweeps.Any(s => (s.Direction == SweepDirection.Bullish) == (direction == SetupDirection.Long));

        var lastEvent = structure.Events.LastOrDefault();
        var ltfConfirmation = lastEvent is not null &&
            (direction == SetupDirection.Long
                ? lastEvent.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch
                : lastEvent.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch);

        var oppositeBoundary = direction == SetupDirection.Long ? rangeHigh : rangeLow;
        var spaceAvailable = atr is not null && Math.Abs(oppositeBoundary - equilibrium) >= atr.Value * 0.5m;

        return new(true, rangeQuality, nearBoundary, sweepOrRejection, ltfConfirmation, spaceAvailable);
    }

    private static bool NearAnyZone(decimal price, IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<FairValueGap> fvgs, SetupDirection direction, decimal tolerance)
    {
        var isLong = direction == SetupDirection.Long;
        var obNear = orderBlocks.Any(o => (o.Direction == OrderBlockDirection.Bullish) == isLong &&
            price >= Math.Min(o.ProximalBoundary, o.DistalBoundary) - tolerance && price <= Math.Max(o.ProximalBoundary, o.DistalBoundary) + tolerance);
        var fvgNear = fvgs.Any(f => (f.Direction == FvgDirection.Bullish) == isLong &&
            price >= f.Lower - tolerance && price <= f.Upper + tolerance);
        return obNear || fvgNear;
    }
}
