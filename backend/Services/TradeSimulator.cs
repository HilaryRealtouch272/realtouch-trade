using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// One executed slice of a paper trade (section 15/17). Units are per FULL
// position; weight them by Fraction to get this exit's contribution.
public record ExitFill(
    string Reason,          // "TP1" | "TP2" | "TP3" | "Stop" | "Time exit"
    decimal LevelPrice,     // the planned level this exit was aiming at
    decimal FillPrice,      // the simulated fill: the level, or worse on a gap; market at a time exit
    DateTime TimeUtc,
    decimal Fraction,       // share of the ORIGINAL position closed by this exit
    decimal? GrossUnits,    // pips/points from the entry fill to this fill
    decimal? CostUnits,     // round trip spread + slippage + commission, in the same units
    decimal? NetUnits,
    decimal R);             // gross R-multiple: (fill - entry) in units of the planned risk

// The one place a paper trade is simulated forward through price: fills,
// costs, partial exits, gaps and same-candle sequencing (sections 14 to 16).
// Used by the live ledger AND the backtester, so a backtest can never quietly
// disagree with how live trades are actually scored.
public static class TradeSimulator
{
    public const string EntryTypeDescription =
        "Limit at the plan's preferred entry, assumed filled (price was in the entry zone at signal time)";

    internal static bool IsOpen(string status) => status is "Open" or "Tp1Hit" or "Tp2Hit";

    // Stop and target FILLS are simulated, not assumed at the intended level:
    // targets are limit orders (fill at the level); a stop is a market order,
    // so it fills at the level unless the market gapped through it, in which
    // case it fills at the gap's open. Spread, slippage and commission are
    // charged as a round-trip cost in movement units (weighted by the fraction
    // each exit closes), never hidden inside a favourable price.
    internal static QualificationLogEntry ApplyPriceAndExpiry(QualificationLogEntry entry, decimal? livePrice, DateTime now)
    {
        entry = ApplyPrice(entry, livePrice, now, stopFill: livePrice, eventTime: null);
        return ApplyExpiry(entry, now, markPrice: livePrice);
    }

    // Walks every completed candle since the trade began, in order, checking
    // each one's real High/Low (not just the latest Close). Within one candle
    // the conservative rule applies - whichever extreme is worse for the
    // position is assumed to have traded first - EXCEPT that when a stop and a
    // target both sit inside the same candle and finer candles are supplied,
    // the sequence is resolved from those; otherwise the trade is flagged
    // IntrabarSequenceUncertain and the stop-first outcome stands.
    internal static QualificationLogEntry ApplyCandleSequence(
        QualificationLogEntry entry, IReadOnlyList<NormalizedCandle> candles, DateTime now,
        IReadOnlyList<NormalizedCandle>? fineCandles = null)
    {
        var relevant = candles
            .Where(c => c.IsComplete && c.OpenTimeUtc >= entry.QualifiedAtUtc)
            .OrderBy(c => c.OpenTimeUtc).ToList();

        foreach (var candle in relevant)
        {
            if (!IsOpen(entry.Status)) break;
            entry = ApplyCandle(entry, candle, now, fineCandles);
        }

        // Time exits are marked to the latest completed close, not assumed flat.
        decimal? mark = relevant.Count > 0 ? relevant[^1].Close : null;
        return ApplyExpiry(entry, now, mark);
    }

    private static QualificationLogEntry ApplyCandle(QualificationLogEntry entry, NormalizedCandle candle, DateTime now, IReadOnlyList<NormalizedCandle>? fine)
    {
        var isLong = entry.Direction == "Long";
        var (adverse, favorable) = isLong ? (candle.Low, candle.High) : (candle.High, candle.Low);
        var eventTime = candle.CloseTimeUtc <= now ? candle.CloseTimeUtc : now;

        var stopReached = isLong ? adverse <= entry.Stop : adverse >= entry.Stop;
        var next = NextTarget(entry);
        var targetReached = next.HasValue && (isLong ? favorable >= next.Value : favorable <= next.Value);

        if (stopReached && targetReached)
        {
            var window = FineWindow(candle, fine);
            if (window is not null)
            {
                foreach (var f in window)
                {
                    if (!IsOpen(entry.Status)) break;
                    entry = ApplyCandle(entry, f, now, null);
                }
                return entry;
            }
            entry = entry with { IntrabarSequenceUncertain = true };
        }

        // A candle that OPENED beyond the stop gapped through it: the stop fills
        // at the open, worse than its level.
        decimal? stopFill = (isLong ? candle.Open <= entry.Stop : candle.Open >= entry.Stop) ? candle.Open : null;
        entry = ApplyPrice(entry, adverse, now, stopFill, eventTime);
        if (IsOpen(entry.Status)) entry = ApplyPrice(entry, favorable, now, null, eventTime);
        return entry;
    }

    private static decimal? NextTarget(QualificationLogEntry e) => e.Status switch
    {
        "Open" => e.Tp1,
        "Tp1Hit" => e.Tp2,
        "Tp2Hit" => e.Tp3,
        _ => null
    };

    // The finer candles that exactly tile this candle, or null when they are
    // missing or incomplete (a partial tiling cannot settle the order).
    private static List<NormalizedCandle>? FineWindow(NormalizedCandle candle, IReadOnlyList<NormalizedCandle>? fine)
    {
        if (fine is null || fine.Count == 0) return null;
        var inside = fine.Where(c => c.IsComplete && c.OpenTimeUtc >= candle.OpenTimeUtc && c.CloseTimeUtc <= candle.CloseTimeUtc)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        if (inside.Count < 2) return null;
        var fineSpan = inside[0].CloseTimeUtc - inside[0].OpenTimeUtc;
        var coarseSpan = candle.CloseTimeUtc - candle.OpenTimeUtc;
        if (fineSpan <= TimeSpan.Zero || fineSpan >= coarseSpan) return null;
        var expected = (int)Math.Round(coarseSpan.TotalSeconds / fineSpan.TotalSeconds);
        return inside.Count >= expected ? inside : null;
    }

    private static QualificationLogEntry ApplyPrice(QualificationLogEntry entry, decimal? livePrice, DateTime now, decimal? stopFill, DateTime? eventTime = null)
    {
        if (!livePrice.HasValue) return entry;
        var price = livePrice.Value;
        var at = eventTime ?? now;
        var isLong = entry.Direction == "Long";
        var riskDistance = Math.Abs(entry.Entry - entry.Stop);
        entry = EnsureLegacyExits(entry);

        // Section 17's MFE/MAE: the running best/worst excursion in R-multiples,
        // updated on every price check whether or not the trade closes.
        if (riskDistance > 0)
        {
            var currentR = isLong ? (price - entry.Entry) / riskDistance : (entry.Entry - price) / riskDistance;
            entry = entry with
            {
                MaxFavorableExcursionR = Math.Max(entry.MaxFavorableExcursionR ?? currentR, currentR),
                MaxAdverseExcursionR = Math.Min(entry.MaxAdverseExcursionR ?? currentR, currentR)
            };
        }

        bool Reached(decimal level) => isLong ? price >= level : price <= level;
        var stopped = isLong ? price <= entry.Stop : price >= entry.Stop;

        if (stopped)
        {
            // The remainder takes the stop (there is no breakeven move in this
            // model). A stop is a market order: it fills at its level, or worse
            // where the market gapped through it - never better than the level.
            var fill = isLong ? Math.Min(stopFill ?? entry.Stop, entry.Stop) : Math.Max(stopFill ?? entry.Stop, entry.Stop);
            entry = CloseRemaining(entry, "Stop", entry.Stop, fill, at);
            var note = "Original stop hit" + (entry.Tp2HitAtUtc is not null ? " after TP1+TP2 already banked" : entry.Tp1HitAtUtc is not null ? " after TP1 already banked" : "");
            if (fill != entry.Stop) note += $" (gapped: filled at {fill} instead of {entry.Stop})";
            return Finalize(entry with { Status = "StoppedOut", StopHitAtUtc = at, ClosedAtUtc = at }, at, note);
        }

        if (Reached(entry.Tp3))
        {
            // Price traversed TP1 and TP2 to reach TP3 even if a scan gap never
            // recorded them separately - all three legs are banked.
            entry = EnsureTarget(entry, "TP1", entry.Tp1, at);
            entry = EnsureTarget(entry, "TP2", entry.Tp2, at);
            entry = CloseRemaining(entry, "TP3", entry.Tp3, entry.Tp3, at);
            return Finalize(entry with { Status = "Tp3Hit", Tp1HitAtUtc = entry.Tp1HitAtUtc ?? at, Tp2HitAtUtc = entry.Tp2HitAtUtc ?? at, Tp3HitAtUtc = at, ClosedAtUtc = at }, at, "Full target (TP3) reached");
        }

        if (Reached(entry.Tp2) && entry.Status != "Tp2Hit")
        {
            // A jump straight past TP1 to TP2 still traversed TP1, so both legs bank.
            entry = EnsureTarget(entry, "TP1", entry.Tp1, at);
            entry = EnsureTarget(entry, "TP2", entry.Tp2, at);
            return entry with { Status = "Tp2Hit", Tp1HitAtUtc = entry.Tp1HitAtUtc ?? at, Tp2HitAtUtc = entry.Tp2HitAtUtc ?? at };
        }

        if (Reached(entry.Tp1) && entry.Status == "Open")
        {
            entry = EnsureTarget(entry, "TP1", entry.Tp1, at);
            return entry with { Status = "Tp1Hit", Tp1HitAtUtc = entry.Tp1HitAtUtc ?? at };
        }

        return entry;
    }

    // Checked once, against the real current time, after any candle walk - so
    // the first candle checked cannot short-circuit the whole walk. The
    // unclosed remainder exits at the latest known price (a rule-based time
    // exit at market), or flat at entry only when no price is known at all.
    private static QualificationLogEntry ApplyExpiry(QualificationLogEntry entry, DateTime now, decimal? markPrice)
    {
        if (!IsOpen(entry.Status) || now <= entry.TrackingExpiryUtc) return entry;

        entry = EnsureLegacyExits(entry);
        var fill = markPrice ?? entry.Entry;
        entry = CloseRemaining(entry, "Time exit", fill, fill, now);
        var hours = (now - entry.QualifiedAtUtc).TotalHours;
        var how = markPrice.HasValue ? $"exited at market {fill}" : "no price available, treated as flat";
        return Finalize(entry with { Status = "Expired", ClosedAtUtc = now }, now,
            $"Tracking window expired ({hours:0.#}h) with no further target reached; remainder {how}");
    }

    // ---- exits, costs and closure ----

    private static (decimal Unit, decimal CostPerFull, string Label)? UnitInfo(QualificationLogEntry e)
    {
        if (!InstrumentMetadataCatalog.TryGet(e.Symbol, out var meta) || meta is null) return null;
        // The cost recorded on the entry at creation wins over today's catalog
        // default, so a later config change cannot rewrite history.
        return (meta.MovementUnitSize, e.EntrySpreadUnits + e.SlippageUnits + e.CommissionUnits, PipCalculator.UnitLabel(meta.MovementUnitName));
    }

    private static decimal Weight(string reason) => reason switch
    {
        "TP1" => EntryStopTargetCalculator.Tp1Weight,
        "TP2" => EntryStopTargetCalculator.Tp2Weight,
        "TP3" => EntryStopTargetCalculator.Tp3Weight,
        _ => 0m
    };

    private static ExitFill MakeExit(QualificationLogEntry entry, string reason, decimal level, decimal fill, DateTime time, decimal fraction)
    {
        var dir = entry.Direction == "Long" ? 1m : -1m;
        var risk = Math.Abs(entry.Entry - entry.Stop);
        var r = risk == 0 ? 0m : dir * (fill - entry.Entry) / risk;
        decimal? gross = null, cost = null, net = null;
        if (UnitInfo(entry) is { } u)
        {
            gross = dir * (fill - entry.Entry) / u.Unit;
            cost = u.CostPerFull;
            net = gross - cost;
        }
        return new ExitFill(reason, level, fill, time, fraction, gross, cost, net, r);
    }

    private static decimal Banked(QualificationLogEntry e) => e.Exits?.Sum(x => x.Fraction) ?? 0m;

    private static QualificationLogEntry EnsureTarget(QualificationLogEntry entry, string reason, decimal level, DateTime time)
    {
        var exits = entry.Exits ?? Array.Empty<ExitFill>();
        if (exits.Any(x => x.Reason == reason)) return entry;
        return entry with { Exits = exits.Append(MakeExit(entry, reason, level, level, time, Weight(reason))).ToList() };
    }

    private static QualificationLogEntry CloseRemaining(QualificationLogEntry entry, string reason, decimal level, decimal fill, DateTime time)
    {
        var remaining = 1m - Banked(entry);
        if (remaining <= 0m) return entry;
        var exits = entry.Exits ?? Array.Empty<ExitFill>();
        return entry with { Exits = exits.Append(MakeExit(entry, reason, level, fill, time, remaining)).ToList() };
    }

    // Rows persisted before exits were recorded only know their TP timestamps;
    // rebuild the banked legs from those so history is not lost or double-counted.
    private static QualificationLogEntry EnsureLegacyExits(QualificationLogEntry entry)
    {
        if (entry.Exits is not null) return entry;
        var exits = new List<ExitFill>();
        if (entry.Tp1HitAtUtc is { } t1) exits.Add(MakeExit(entry, "TP1", entry.Tp1, entry.Tp1, t1, Weight("TP1")));
        if (entry.Tp2HitAtUtc is { } t2) exits.Add(MakeExit(entry, "TP2", entry.Tp2, entry.Tp2, t2, Weight("TP2")));
        return entry with { Exits = exits };
    }

    // Closure-time totals, computed once from the recorded exits (sections 14
    // and 17): gross R and net R (costs converted to R), gross/cost/net
    // movement, monetary P&L and return on NET R, the outcome category, and
    // holding duration.
    internal static QualificationLogEntry Finalize(QualificationLogEntry entry, DateTime closedAtUtc, string closureReason)
    {
        entry = EnsureLegacyExits(entry);
        var exits = entry.Exits!;
        var risk = Math.Abs(entry.Entry - entry.Stop);
        var grossR = exits.Sum(x => x.Fraction * x.R);

        decimal? gross = null, cost = null, net = null;
        string? unitLabel = null;
        var costR = 0m;
        if (UnitInfo(entry) is { } u)
        {
            gross = exits.Sum(x => x.Fraction * (x.GrossUnits ?? 0m));
            cost = exits.Sum(x => x.Fraction * (x.CostUnits ?? 0m));
            net = gross - cost;
            unitLabel = u.Label;
            costR = risk == 0 ? 0m : cost.Value * u.Unit / risk;
        }
        var netR = grossR - costR;

        var outcome = Math.Abs(netR) <= 0.05m ? "Breakeven"
            : entry.Status == "Tp3Hit" ? "TP3 Win"
            : entry.Tp2HitAtUtc is not null ? "TP2 Partial Win"
            : entry.Tp1HitAtUtc is not null ? "TP1 Partial Win"
            : entry.Status == "StoppedOut" ? "Stopped Out"
            : "Rule-Based Exit";

        return entry with
        {
            RealizedR = grossR, NetRealizedR = netR,
            GrossMovementUnits = gross, CostMovementUnits = cost, NetMovementUnits = net, MovementUnitLabel = unitLabel,
            MonetaryPnL = entry.RiskAmount * netR, PercentageReturn = entry.RiskPercent * netR,
            FinalOutcome = outcome, ClosureReason = closureReason,
            HoldingDurationHours = (closedAtUtc - entry.QualifiedAtUtc).TotalHours
        };
    }
}
