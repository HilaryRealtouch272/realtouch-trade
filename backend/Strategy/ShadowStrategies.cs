using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Four additional strategies that run in SHADOW MODE only: they are evaluated and
// recorded, never alerted and never entered into the paper ledger. Promotion to
// qualified paper trading needs deterministic tests, out-of-sample backtest
// results and a documented minimum quality (see STRATEGY.md) - none of which a new
// model has on day one. No quota of signals is forced for any of them.
public enum ShadowModel
{
    SessionLiquiditySweepMss,
    VolatilityContractionBreakoutRetest,
    ZoneMitigation,
    FailedBreakoutReclaim
}

public record ShadowCandidate(
    ShadowModel Model, SetupDirection Direction, int Score, IReadOnlyList<string> Evidence, IReadOnlyList<string> Missing,
    decimal Entry, decimal Stop, decimal Tp1, decimal Tp2, decimal Tp3, DateTime DetectedAtUtc);

public static class ShadowStrategies
{
    // A shadow candidate needs all of its mandatory evidence AND this much score.
    public const int MinScore = 70;
    // Only setups whose final confirmation is recent are reported, so a stale
    // pattern is not re-reported on every scan.
    private const int FreshnessCandles = 4;

    private record Ev(bool Mandatory, int Points, bool Met, string Text);

    public static IReadOnlyList<ShadowCandidate> Evaluate(
        IReadOnlyList<NormalizedCandle> allCandles, Timeframe tf, StructureResult structure,
        IReadOnlyList<OrderBlock> obs, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<KeyLevel> keyLevels,
        decimal minStopCostFloor = 0m)
    {
        var c = allCandles.Where(x => x.IsComplete && x.Quality == DataQuality.Ok).OrderBy(x => x.OpenTimeUtc).ToList();
        if (c.Count < 60) return Array.Empty<ShadowCandidate>();
        var atr = Indicators.Atr(c, 14);
        var results = new List<ShadowCandidate>();

        foreach (var direction in new[] { SetupDirection.Long, SetupDirection.Short })
        {
            Add(results, SessionSweep(c, tf, structure, atr, direction, minStopCostFloor));
            Add(results, ContractionBreakout(c, atr, keyLevels, direction, minStopCostFloor));
            Add(results, ZoneMitigation(c, structure, obs, fvgs, atr, direction, minStopCostFloor));
            Add(results, FailedBreakout(c, atr, keyLevels, direction, minStopCostFloor));
        }
        return results;
    }

    private static void Add(List<ShadowCandidate> list, ShadowCandidate? candidate)
    {
        if (candidate is not null) list.Add(candidate);
    }

    // ---- 1. Session Liquidity Sweep and Market Structure Shift ----------------------
    // The Asian range (00:00-07:00 UTC) is swept in London/New York hours, price closes
    // back inside, then a CHoCH in the new direction, displacement and a controlled retest.
    internal static ShadowCandidate? SessionSweep(List<NormalizedCandle> c, Timeframe tf, StructureResult structure,
        decimal?[] atr, SetupDirection direction, decimal costFloor)
    {
        var isLong = direction == SetupDirection.Long;
        var last = c[^1];
        var day = last.CloseTimeUtc.Date;
        var box = c.Where(x => x.OpenTimeUtc.Date == day && x.OpenTimeUtc.Hour < 7).ToList();
        if (box.Count < 3 || atr[^1] is not { } a) return null;
        var boxHigh = box.Max(x => x.High);
        var boxLow = box.Min(x => x.Low);

        // A sweep of the low is a long setup; a sweep of the high is a short.
        var sweepIndex = -1;
        for (var i = c.Count - 1; i >= 0 && c[i].OpenTimeUtc.Date == day; i--)
        {
            var x = c[i];
            if (x.OpenTimeUtc.Hour < 7 || x.OpenTimeUtc.Hour >= 20) continue;
            var swept = isLong ? x.Low < boxLow && x.Close > boxLow : x.High > boxHigh && x.Close < boxHigh;
            if (swept) { sweepIndex = i; break; }
        }
        if (sweepIndex < 0) return null;
        var sweep = c[sweepIndex];
        var extreme = isLong ? sweep.Low : sweep.High;

        var choch = structure.Events.LastOrDefault(e => e.CandleTimeUtc > sweep.OpenTimeUtc &&
            e.Type == (isLong ? StructureEventType.BullishChoch : StructureEventType.BearishChoch));
        var chochIndex = choch is null ? -1 : c.FindLastIndex(x => x.CloseTimeUtc == choch.CandleTimeUtc);
        var fresh = chochIndex >= 0 && c.Count - 1 - chochIndex <= FreshnessCandles;

        var displaced = false;
        for (var i = Math.Max(sweepIndex, 21); i < c.Count; i++)
            if (Displacement.IsDisplacementCandle(c, i) && (c[i].Close > c[i].Open) == isLong) { displaced = true; break; }
        var retest = choch is not null && Math.Abs(last.Close - choch.BrokenPivot.Price) <= 0.75m * a;

        return Build(ShadowModel.SessionLiquiditySweepMss, direction, last, a, extreme, costFloor, new[]
        {
            new Ev(true, 30, true, $"{(isLong ? "Asian low" : "Asian high")} swept in session hours"),
            new Ev(true, 20, true, "Completed close back inside the Asian range"),
            new Ev(true, 25, fresh, "CHoCH in the new direction after the sweep"),
            new Ev(false, 15, displaced, "Displacement candle in the new direction"),
            new Ev(false, 10, retest, "Controlled retest of the CHoCH level"),
        });
    }

    // ---- 2. Volatility Contraction Breakout and Retest ------------------------------
    internal static ShadowCandidate? ContractionBreakout(List<NormalizedCandle> c, decimal?[] atr,
        IReadOnlyList<KeyLevel> keyLevels, SetupDirection direction, decimal costFloor)
    {
        var isLong = direction == SetupDirection.Long;
        var n = c.Count;
        var refIdx = n - 6; // the bar just before the breakout window
        if (refIdx < 56 || atr[refIdx] is not { } refAtr || refAtr <= 0) return null;

        var prior = Enumerable.Range(refIdx - 50, 50).Select(i => atr[i]).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (prior.Count < 40) return null;
        var contracted = refAtr < 0.75m * prior.Average();

        var window = c.GetRange(refIdx - 24, 25);
        var rangeHigh = window.Max(x => x.High);
        var rangeLow = window.Min(x => x.Low);
        var mature = (rangeHigh - rangeLow) <= 4m * refAtr;

        // The decisive candle: closes beyond the range with a big body and an ATR expansion.
        var breakIdx = -1;
        for (var i = refIdx + 1; i < n; i++)
        {
            var x = c[i];
            var range = x.High - x.Low;
            if (range == 0) continue;
            var body = Math.Abs(x.Close - x.Open) / range;
            var beyond = isLong ? x.Close > rangeHigh + 0.15m * refAtr : x.Close < rangeLow - 0.15m * refAtr;
            if (beyond && body >= 0.6m && range >= 1.5m * refAtr) { breakIdx = i; break; }
        }
        if (breakIdx < 0) return null;

        var edge = isLong ? rangeHigh : rangeLow;
        var retested = false;
        for (var i = breakIdx + 1; i < n; i++)
        {
            var x = c[i];
            var touched = isLong ? x.Low <= edge + 0.25m * refAtr : x.High >= edge - 0.25m * refAtr;
            var held = isLong ? x.Close > edge : x.Close < edge;
            if (touched && held) retested = true;
        }
        var last = c[^1];
        var stayed = isLong ? last.Close > edge : last.Close < edge;

        var stop = isLong ? rangeLow + (rangeHigh - rangeLow) / 2 : rangeHigh - (rangeHigh - rangeLow) / 2; // mid-range invalidation
        // Clear space: no opposing key level within 2R of entry.
        var risk = Math.Abs(last.Close - stop);
        var clear = risk > 0 && !keyLevels.Any(l => !l.Invalidated &&
            (isLong ? l.Upper > last.Close && l.Upper < last.Close + 2 * risk : l.Lower < last.Close && l.Lower > last.Close - 2 * risk));

        return Build(ShadowModel.VolatilityContractionBreakoutRetest, direction, last, refAtr, stop, costFloor, new[]
        {
            new Ev(true, 25, contracted, "ATR contracted to under 75% of its 50-bar average"),
            new Ev(true, 15, mature, "Range of at least 20 candles, tight relative to ATR"),
            new Ev(true, 20, true, "ATR-expanding decisive close beyond the range (body 60%+)"),
            new Ev(true, 20, retested && stayed, "Retest of the range edge that held"),
            new Ev(true, 20, clear, "No opposing key level within 2R"),
        }, structureFreshOk: breakIdx >= n - 1 - 5);
    }

    // ---- 3. Zone Mitigation (fresh zone + premium/discount + reaction + confirmation) ----
    // NOTE: uses the setup timeframe's own zones. The brief's version reads fresh Daily/4H zones with
    // a 15m confirmation; that needs higher-timeframe zone detection, which is not built yet.
    internal static ShadowCandidate? ZoneMitigation(List<NormalizedCandle> c, StructureResult structure,
        IReadOnlyList<OrderBlock> obs, IReadOnlyList<FairValueGap> fvgs, decimal?[] atr, SetupDirection direction, decimal costFloor)
    {
        var isLong = direction == SetupDirection.Long;
        if (atr[^1] is not { } a) return null;
        var last = c[^1];

        var zones = new List<(decimal Lo, decimal Hi, DateTime Origin)>();
        zones.AddRange(obs.Where(o => OrderBlockDetector.IsValid(o) && (o.Direction == OrderBlockDirection.Bullish) == isLong)
            .Select(o => (Math.Min(o.ProximalBoundary, o.DistalBoundary), Math.Max(o.ProximalBoundary, o.DistalBoundary), o.CandleTimeUtc)));
        zones.AddRange(fvgs.Where(f => f.Status == MitigationStatus.Unmitigated && (f.Direction == FvgDirection.Bullish) == isLong)
            .Select(f => (f.Lower, f.Upper, f.OriginCandleTimeUtc)));

        var recent = c.Skip(c.Count - 4).ToList();
        var touching = zones.Where(z => recent.Any(x => x.Low <= z.Hi && x.High >= z.Lo)).OrderByDescending(z => z.Origin).ToList();
        if (touching.Count == 0) return null;
        var zone = touching[0];

        var window = c.Skip(c.Count - 50).ToList();
        var mid = (window.Max(x => x.High) + window.Min(x => x.Low)) / 2;
        var aligned = isLong ? last.Close < mid : last.Close > mid;
        var reacted = isLong ? last.Close > zone.Lo && recent.Any(x => x.Low <= zone.Hi && x.Close > (zone.Lo + zone.Hi) / 2)
                             : last.Close < zone.Hi && recent.Any(x => x.High >= zone.Lo && x.Close < (zone.Lo + zone.Hi) / 2);
        var confirm = structure.Events.LastOrDefault(e => e.CandleTimeUtc >= recent[0].CloseTimeUtc &&
            (isLong ? e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch
                    : e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch));
        var displaced = Enumerable.Range(c.Count - 4, 4).Any(i => Displacement.IsDisplacementCandle(c, i) && (c[i].Close > c[i].Open) == isLong);

        var stop = isLong ? zone.Lo : zone.Hi;
        return Build(ShadowModel.ZoneMitigation, direction, last, a, stop, costFloor, new[]
        {
            new Ev(true, 25, true, "Fresh, unmitigated order block or FVG being mitigated now"),
            new Ev(true, 20, aligned, isLong ? "Discount side of the recent range" : "Premium side of the recent range"),
            new Ev(true, 20, reacted, "Liquidity reaction: price rejected out of the zone"),
            new Ev(true, 25, confirm is not null, "BOS/CHoCH confirmation in the direction"),
            new Ev(false, 10, displaced, "Displacement candle"),
        });
    }

    // ---- 4. Failed Breakout and Reclaim ----------------------------------------------
    internal static ShadowCandidate? FailedBreakout(List<NormalizedCandle> c, decimal?[] atr,
        IReadOnlyList<KeyLevel> keyLevels, SetupDirection direction, decimal costFloor)
    {
        // A failed break ABOVE a level is a short; a failed break BELOW is a long.
        var isLong = direction == SetupDirection.Long;
        if (atr[^1] is not { } a) return null;
        var n = c.Count;
        var last = c[^1];

        var levels = keyLevels.Where(l => !l.Invalidated).Select(l => isLong ? l.Lower : l.Upper).Where(p => p > 0).Distinct().ToList();

        foreach (var level in levels.OrderBy(l => Math.Abs(l - last.Close)))
        {
            // The break: a close beyond the level within the last 12 candles...
            var breakIdx = -1;
            for (var i = n - 12; i < n - 1; i++)
                if (i > 0 && (isLong ? c[i].Close < level && c[i - 1].Close >= level : c[i].Close > level && c[i - 1].Close <= level)) { breakIdx = i; break; }
            if (breakIdx < 0) continue;

            // ...that fails: a close back on the original side within 3 candles = the reclaim.
            var reclaimIdx = -1;
            for (var i = breakIdx + 1; i <= Math.Min(breakIdx + 3, n - 1); i++)
                if (isLong ? c[i].Close > level : c[i].Close < level) { reclaimIdx = i; break; }
            if (reclaimIdx < 0 || n - 1 - reclaimIdx > FreshnessCandles) continue;

            var displaced = false;
            for (var i = Math.Max(reclaimIdx, 21); i < n; i++)
                if (Displacement.IsDisplacementCandle(c, i) && (c[i].Close > c[i].Open) == isLong) { displaced = true; break; }
            var retest = false;
            for (var i = reclaimIdx + 1; i < n; i++)
            {
                var touched = isLong ? c[i].Low <= level + 0.25m * a : c[i].High >= level - 0.25m * a;
                var held = isLong ? c[i].Close > level : c[i].Close < level;
                if (touched && held) retest = true;
            }

            var extreme = c.Skip(breakIdx).Take(reclaimIdx - breakIdx + 1).Select(x => isLong ? x.Low : x.High);
            var stop = isLong ? extreme.Min() : extreme.Max();
            return Build(ShadowModel.FailedBreakoutReclaim, direction, last, a, stop, costFloor, new[]
            {
                new Ev(true, 20, true, "Break beyond a key level"),
                new Ev(true, 25, true, "Failure to hold: closed back within 3 candles"),
                new Ev(true, 20, true, "Reclaim of the level"),
                new Ev(true, 20, displaced, "Opposing displacement after the reclaim"),
                new Ev(false, 15, retest, "Retest of the level that held"),
            });
        }
        return null;
    }

    // ---- shared: turn evidence into a scored candidate with a plan -----------------
    private static ShadowCandidate? Build(ShadowModel model, SetupDirection direction, NormalizedCandle last, decimal atr,
        decimal structuralStop, decimal costFloor, IReadOnlyList<Ev> evidence, bool structureFreshOk = true)
    {
        if (!structureFreshOk) return null;
        if (evidence.Any(e => e.Mandatory && !e.Met)) return null;
        var score = evidence.Where(e => e.Met).Sum(e => e.Points);
        if (score < MinScore) return null;

        var isLong = direction == SetupDirection.Long;
        var entry = last.Close;
        var buffer = 0.1m * atr;
        var stop = isLong ? structuralStop - buffer : structuralStop + buffer;
        var minDistance = EntryStopTargetCalculator.MinimumStopDistance(atr, costFloor);
        if (Math.Abs(entry - stop) < minDistance) stop = isLong ? entry - minDistance : entry + minDistance;
        var risk = Math.Abs(entry - stop);
        var correctSide = isLong ? stop < entry : stop > entry;
        if (!correctSide || risk <= 0 || risk > EntryStopTargetCalculator.MaxRationalStopAtrMultiple * atr) return null;

        decimal Tp(int r) => isLong ? entry + risk * r : entry - risk * r;
        return new ShadowCandidate(model, direction, score,
            evidence.Where(e => e.Met).Select(e => e.Text).ToList(), evidence.Where(e => !e.Met).Select(e => e.Text).ToList(),
            entry, stop, Tp(1), Tp(2), Tp(3), last.CloseTimeUtc);
    }
}
