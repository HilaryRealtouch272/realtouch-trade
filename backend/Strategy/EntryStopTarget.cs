using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Section 16: where and when to enter. EntryTimeframe here is the SAME
// timeframe the setup was detected on - true "context TF / entry TF" per
// section 7's hierarchy table needs multi-timeframe candle fetching, which
// isn't wired yet, so this is an explicit v1 simplification, not the full rule.
public record EntryPlan(
    decimal ZoneMin,
    decimal ZoneMax,
    decimal PreferredEntry,
    decimal Trigger,
    DateTime? TriggerCandleTimeUtc,
    Timeframe EntryTimeframe,
    DateTime ExpiryUtc,
    decimal InvalidationPrice,
    bool Triggered,
    string ZoneSource // "OrderBlock", "WillisZone", or "FairValueGap"
);

// Section 17: structural stop with a volatility buffer.
public record StopPlan(decimal Price, string Reason, bool IsRational);

// Section 18: TP1/TP2/TP3 with the spec's default 25/50/25 scaling.
public record TargetPlan(
    decimal Tp1, string Tp1Basis,
    decimal Tp2, string Tp2Basis,
    decimal Tp3, string Tp3Basis,
    decimal Tp1Weight, decimal Tp2Weight, decimal Tp3Weight
);

public record TradePlan(EntryPlan Entry, StopPlan Stop, TargetPlan Targets, decimal RewardToRisk);

public static class EntryStopTargetCalculator
{
    private const decimal StopAtrBuffer = 0.10m;
    private const int ExpiryCandles = 10;
    // A stop tighter than this is inside ordinary candle noise (and, on FX,
    // inside the spread): it stops out on nothing and, because reward is
    // measured in multiples of the stop, produces absurd reward-to-risk
    // (live trades showed 17.5 and 20.9 from stops of 21 BTC points and half
    // a pip) that inflate the score. A too-tight stop is WIDENED to this
    // minimum rather than rejected, so the setup survives with a real,
    // tradeable stop and an honest reward-to-risk.
    internal const decimal MinStopAtrMultiple = 0.5m;
    internal const decimal MaxRationalStopAtrMultiple = 8m;

    // The smallest tradeable stop distance: at least MinStopAtrMultiple ATR,
    // and never less than costFloor (a multiple of the instrument's spread
    // plus slippage, supplied by the caller) so costs cannot eat the risk.
    internal static decimal MinimumStopDistance(decimal atr, decimal costFloor) => Math.Max(MinStopAtrMultiple * atr, costFloor);

    internal static StopPlan EnforceMinimumStop(StopPlan stop, decimal entry, bool isLong, decimal atr, decimal costFloor)
    {
        var minimum = MinimumStopDistance(atr, costFloor);
        if (Math.Abs(entry - stop.Price) >= minimum) return stop;

        var widened = isLong ? entry - minimum : entry + minimum;
        // A floor beyond the upper sanity bound means costs alone make this
        // instrument untradeable on this timeframe: no plan, not a huge stop.
        return new StopPlan(widened, stop.Reason + ", widened to the minimum tradeable stop distance", IsRational: minimum <= MaxRationalStopAtrMultiple * atr);
    }

    // How far, in ATR, a zone may sit from the last close and still be a
    // realistic entry. A plan is only live for ExpiryCandles candles, and
    // over 10 candles price typically travels about 3 ATR (one-sigma
    // excursion scales with the square root of time), so a zone farther
    // than that is not a setup price is going to reach - it is a stale
    // level. Without this bound, a real CAKE/USDT 4H Long was planned with
    // its entry 24% below the market: it could never trigger, and its
    // enormous distance inflated the reward-to-risk (5.5) and the score
    // built on it.
    private const decimal MaxEntryDistanceAtrMultiple = 3m;

    // Position-scaling weights per target - public so anything computing
    // real blended P&L on a partially-resolved trade (see
    // Services/SignalLogService.cs) uses the exact same split rather than
    // duplicating these as separate magic numbers.
    public const decimal Tp1Weight = 0.25m;
    public const decimal Tp2Weight = 0.50m;
    public const decimal Tp3Weight = 0.25m;

    public static TradePlan? Compute(
        IReadOnlyList<NormalizedCandle> allCandles,
        Timeframe timeframe,
        SetupDirection direction,
        StructureResult structure,
        IReadOnlyList<OrderBlock> orderBlocks,
        IReadOnlyList<FairValueGap> fvgs,
        IReadOnlyList<RealtouchWillisZone> willisZones,
        IReadOnlyList<LiquiditySweep> sweeps,
        IReadOnlyList<KeyLevel> keyLevels,
        decimal minStopCostFloor = 0m)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        if (completed.Count < 30) return null;
        var atr = Indicators.Atr(completed, 14)[^1];
        if (atr is null) return null;

        var isLong = direction == SetupDirection.Long;
        var zone = ChooseZone(isLong, orderBlocks, willisZones, fvgs, completed[^1].Close, atr.Value * MaxEntryDistanceAtrMultiple);
        if (zone is null) return null;

        var entry = BuildEntryPlan(zone.Value, completed, timeframe, isLong, structure);
        var stop = BuildStopPlan(zone.Value, isLong, atr.Value, orderBlocks, sweeps, entry.PreferredEntry);
        if (!stop.IsRational) return null;
        stop = EnforceMinimumStop(stop, entry.PreferredEntry, isLong, atr.Value, minStopCostFloor);
        if (!stop.IsRational) return null;

        var targets = BuildTargetPlan(entry.PreferredEntry, stop.Price, isLong, keyLevels, atr.Value);
        var risk = Math.Abs(entry.PreferredEntry - stop.Price);
        var reward = Math.Abs(targets.Tp2 - entry.PreferredEntry); // R:R quoted against TP2, the "logical" objective
        var rr = risk == 0 ? 0 : reward / risk;

        return new TradePlan(entry, stop, targets, rr);
    }

    private readonly record struct ZoneChoice(decimal Min, decimal Max, DateTime OriginUtc, string Source);

    // A zone is a realistic entry only if price does not have to travel more
    // than maxDistance to reach it. A Long needs price to come DOWN to the
    // zone's top edge; a Short needs it to come UP to the zone's bottom edge.
    // Price already inside or beyond the zone counts as reachable (distance
    // is not positive) - that case is left to the existing invalidation rules.
    internal static bool IsReachable(bool isLong, decimal lastClose, decimal zoneMin, decimal zoneMax, decimal maxDistance) =>
        isLong ? lastClose - zoneMax <= maxDistance : zoneMin - lastClose <= maxDistance;

    private static ZoneChoice? ChooseZone(bool isLong, IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<RealtouchWillisZone> willisZones, IReadOnlyList<FairValueGap> fvgs,
        decimal lastClose, decimal maxDistance)
    {
        // Prefer a Willis Zone (the richest, most-confirmed zone type), then a
        // plain order block, then a bare FVG - matches "first qualified FVG or
        // the overlapping order-block zone" (section 16) with the Willis Zone
        // (order block + FVG overlap + retracement) taking priority when present.
        // Within a type, the most recent zone price can actually reach wins; a
        // preferred type whose zones are all out of reach falls through to the
        // next type rather than planning an entry price will never see.
        var willis = willisZones.LastOrDefault(z => (z.Direction == OrderBlockDirection.Bullish) == isLong &&
            IsReachable(isLong, lastClose, Math.Min(z.ProximalBoundary, z.DistalBoundary), Math.Max(z.ProximalBoundary, z.DistalBoundary), maxDistance));
        if (willis is not null)
            return new ZoneChoice(Math.Min(willis.ProximalBoundary, willis.DistalBoundary), Math.Max(willis.ProximalBoundary, willis.DistalBoundary), willis.CandleTimeUtc, "WillisZone");

        var ob = orderBlocks.LastOrDefault(o => OrderBlockDetector.IsValid(o) && (o.Direction == OrderBlockDirection.Bullish) == isLong &&
            IsReachable(isLong, lastClose, Math.Min(o.ProximalBoundary, o.DistalBoundary), Math.Max(o.ProximalBoundary, o.DistalBoundary), maxDistance));
        if (ob is not null)
            return new ZoneChoice(Math.Min(ob.ProximalBoundary, ob.DistalBoundary), Math.Max(ob.ProximalBoundary, ob.DistalBoundary), ob.CandleTimeUtc, "OrderBlock");

        var fvg = fvgs.LastOrDefault(f => f.Status != MitigationStatus.Invalidated && (f.Direction == FvgDirection.Bullish) == isLong &&
            IsReachable(isLong, lastClose, f.Lower, f.Upper, maxDistance));
        if (fvg is not null)
            return new ZoneChoice(fvg.Lower, fvg.Upper, fvg.OriginCandleTimeUtc, "FairValueGap");

        return null;
    }

    private static EntryPlan BuildEntryPlan(ZoneChoice zone, List<NormalizedCandle> completed, Timeframe timeframe, bool isLong, StructureResult structure)
    {
        var preferred = (zone.Min + zone.Max) / 2; // midpoint, per section 16 rule 5
        var duration = TimeframeConfig.Duration(timeframe);
        var expiry = zone.OriginUtc + TimeSpan.FromTicks(duration.Ticks * ExpiryCandles);

        // Triggered = price has actually traded into the zone AND a matching-direction
        // structure event occurred at/after the zone's origin (the CHoCH/BOS trigger).
        var triggerEvent = structure.Events.LastOrDefault(e => e.CandleTimeUtc >= zone.OriginUtc &&
            ((isLong && e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch) ||
             (!isLong && e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch)));

        var lastClose = completed[^1].Close;
        var priceInZone = lastClose >= zone.Min && lastClose <= zone.Max;
        var triggered = triggerEvent is not null && priceInZone;

        return new EntryPlan(
            zone.Min, zone.Max, preferred,
            Trigger: preferred,
            TriggerCandleTimeUtc: triggerEvent?.CandleTimeUtc,
            EntryTimeframe: timeframe,
            ExpiryUtc: expiry,
            InvalidationPrice: isLong ? zone.Min : zone.Max,
            Triggered: triggered,
            ZoneSource: zone.Source
        );
    }

    // entryPrice is the actual PreferredEntry (zone midpoint) the stop has to
    // protect - NOT the latest live close. A forming/Watchlist setup can have
    // its live price sitting well away from the zone (it hasn't triggered
    // yet), so validating candidates against the live price instead of the
    // real entry could pick - and previously did pick, for real deployed
    // setups - a candidate that sits on the WRONG side of entry while still
    // technically satisfying a "below/above the live price" check. That
    // produced a Long with its stop above entry (and, as a direct
    // consequence, TP1 landing exactly on the stop) - a real, live bug, not
    // a hypothetical one.
    private static StopPlan BuildStopPlan(ZoneChoice zone, bool isLong, decimal atr, IReadOnlyList<OrderBlock> orderBlocks, IReadOnlyList<LiquiditySweep> sweeps, decimal entryPrice)
    {
        var buffer = StopAtrBuffer * atr;
        var candidates = new List<(decimal price, string reason)>();

        var matchingOb = orderBlocks.LastOrDefault(o => (o.Direction == OrderBlockDirection.Bullish) == isLong);
        if (matchingOb is not null)
            candidates.Add((matchingOb.DistalBoundary, "order-block distal boundary"));

        var matchingSweep = sweeps.LastOrDefault(s => (s.Direction == SweepDirection.Bullish) == isLong);
        if (matchingSweep is not null)
            candidates.Add((matchingSweep.SweptLevel, "liquidity-sweep level"));

        candidates.Add((isLong ? zone.Min : zone.Max, "zone boundary (fallback - no order block or sweep available)"));

        // For a long, the stop is the nearest structural invalidation BELOW
        // entry (the highest of the below-entry candidates); for a short,
        // the inverse. Candidates on the wrong side of entry are excluded
        // outright, not just deprioritized.
        var chosen = isLong
            ? candidates.Where(c => c.price < entryPrice).OrderByDescending(c => c.price).FirstOrDefault(candidates[^1])
            : candidates.Where(c => c.price > entryPrice).OrderBy(c => c.price).FirstOrDefault(candidates[^1]);

        var price = isLong ? chosen.price - buffer : chosen.price + buffer;
        var distance = Math.Abs(entryPrice - price);
        // The directional check is a hard requirement, independent of the
        // ATR-distance sanity check below - a correctly-sized stop on the
        // wrong side of entry is not "a bit off", it's a fundamentally
        // invalid trade plan and must never qualify as rational.
        var correctSide = isLong ? price < entryPrice : price > entryPrice;
        // Too-TIGHT stops are widened afterwards (EnforceMinimumStop), not rejected.
        var isRational = correctSide && atr > 0 && distance <= MaxRationalStopAtrMultiple * atr;

        return new StopPlan(price, $"{chosen.reason}, plus {StopAtrBuffer}xATR(14) buffer", isRational);
    }

    internal static TargetPlan BuildTargetPlan(decimal entry, decimal stop, bool isLong, IReadOnlyList<KeyLevel> keyLevels, decimal atr)
    {
        var riskDistance = Math.Abs(entry - stop);
        var tp1 = isLong ? entry + riskDistance : entry - riskDistance; // ~1R, section 18 default

        // TP2: nearest opposing key level beyond 1R, else a flat 2R.
        var opposingLevels = keyLevels
            .Where(l => !l.Invalidated)
            .Select(l => isLong ? l.Upper : l.Lower)
            .Where(p => isLong ? p > tp1 : p < tp1)
            .ToList();
        var tp2 = opposingLevels.Count > 0
            ? (isLong ? opposingLevels.Min() : opposingLevels.Max())
            : (isLong ? entry + riskDistance * 2 : entry - riskDistance * 2);
        var tp2Basis = opposingLevels.Count > 0 ? "nearest opposing key level" : "2R projection (no further key level found)";

        // TP3: the furthest opposing key level beyond TP2 ("external liquidity"), else 3R.
        var externalLevels = keyLevels
            .Where(l => !l.Invalidated)
            .Select(l => isLong ? l.Upper : l.Lower)
            .Where(p => isLong ? p > tp2 : p < tp2)
            .ToList();
        // The 3R fallback must still land BEYOND TP2: TP2 can come from a far
        // key level (well past 3R), and a flat 3R would then put TP3 on the
        // wrong side of it (a Long with TP3 below TP2) - a target order that
        // cannot happen, which would also make the ledger credit TP3 before
        // TP2. At least 1R past TP2 in that case.
        var tp3 = externalLevels.Count > 0
            ? (isLong ? externalLevels.Max() : externalLevels.Min())
            : (isLong ? Math.Max(entry + riskDistance * 3, tp2 + riskDistance) : Math.Min(entry - riskDistance * 3, tp2 - riskDistance));
        var tp3Basis = externalLevels.Count > 0 ? "external/higher-timeframe liquidity level" : "3R measured-move projection, at least 1R beyond TP2 (no further key level found)";

        return new TargetPlan(tp1, "~1R (nearest opposing internal liquidity)", tp2, tp2Basis, tp3, tp3Basis, Tp1Weight, Tp2Weight, Tp3Weight);
    }
}
