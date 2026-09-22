using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Range Boundary Rejection needs its own trade plan, not the pullback
// planner's zone-based one - real audit finding: EntryStopTargetCalculator
// only knows how to plan off an order block/Willis Zone/FVG, which a range
// boundary often doesn't have sitting exactly on it. Section 6.4 explicitly
// requires the target to be the range's own equilibrium or opposite
// boundary, not a generic key-level projection, so this builds that
// directly from the same RangeEvidence the scorer already computed.
public static class RangeTradePlan
{
    private const decimal StopAtrBuffer = 0.10m;

    public static TradePlan? Build(
        IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, SetupDirection direction, DateTime lastCloseTimeUtc)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok).OrderBy(c => c.OpenTimeUtc).ToList();
        const int rangeLookback = 20;
        if (completed.Count < rangeLookback) return null;

        var window = completed.Skip(completed.Count - rangeLookback).ToList();
        var rangeHigh = window.Max(c => c.High);
        var rangeLow = window.Min(c => c.Low);
        var equilibrium = (rangeHigh + rangeLow) / 2;
        var atr = Indicators.Atr(completed, 14)[^1];
        if (atr is null || atr.Value <= 0) return null;

        var isLong = direction == SetupDirection.Long;
        var boundary = isLong ? rangeLow : rangeHigh;
        var oppositeBoundary = isLong ? rangeHigh : rangeLow;
        var buffer = StopAtrBuffer * atr.Value;

        var preferredEntry = boundary;
        var stopPrice = isLong ? boundary - buffer : boundary + buffer;
        var riskDistance = Math.Abs(preferredEntry - stopPrice);
        if (riskDistance <= 0) return null;

        var entry = new EntryPlan(
            ZoneMin: Math.Min(boundary, preferredEntry) - buffer, ZoneMax: Math.Max(boundary, preferredEntry) + buffer,
            PreferredEntry: preferredEntry, Trigger: preferredEntry, TriggerCandleTimeUtc: lastCloseTimeUtc,
            EntryTimeframe: timeframe, ExpiryUtc: lastCloseTimeUtc + TimeframeConfig.Duration(timeframe) * 10,
            InvalidationPrice: stopPrice, Triggered: true, ZoneSource: "RangeBoundary");

        var stop = new StopPlan(stopPrice, "range boundary, plus 0.10xATR(14) buffer", IsRational: true);

        var tp1 = isLong ? preferredEntry + riskDistance : preferredEntry - riskDistance; // ~1R
        var targets = new TargetPlan(
            tp1, "~1R (initial partial)",
            equilibrium, "range equilibrium",
            oppositeBoundary, "opposite range boundary",
            EntryStopTargetCalculator.Tp1Weight, EntryStopTargetCalculator.Tp2Weight, EntryStopTargetCalculator.Tp3Weight);

        var reward = Math.Abs(targets.Tp2 - preferredEntry);
        var rr = riskDistance == 0 ? 0 : reward / riskDistance;

        return new TradePlan(entry, stop, targets, rr);
    }
}
