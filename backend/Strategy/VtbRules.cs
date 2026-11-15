using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Validated Trend Breakout (VTB): a daily SMA200-trend-filter + Donchian-style channel
// breakout, ported directly from the verified reference implementation (vtb_scanner.py's
// `indicators`/`replay`) in the Realtouch_VTB_Developer_Package - not reconstructed from the
// brief's pseudocode, which had a since-fixed ordering bug the real Python code never had.
// Deliberately a self-contained rule set, independent of the real four-model engine: fixed
// stop/target/time-exit, no zones, no confluence scoring - a different strategy family.
public enum VtbSide { Long, Short }
public enum VtbExitReason { Stop, Target, Time }

public record VtbDailyIndicators(decimal Close, decimal Sma200, decimal Atr14, decimal HhN, decimal LlN, bool HasFullHistory);

public record VtbPosition(VtbSide Side, DateTime EntryTimeUtc, decimal EntryRef, decimal Risk, decimal Stop, decimal Target);

public record VtbClosedTrade(VtbSide Side, DateTime EntryTimeUtc, decimal EntryRef, decimal Stop, decimal Target, decimal Risk,
    DateTime ExitTimeUtc, decimal Exit, VtbExitReason Reason, decimal R);

public static class VtbRules
{
    public const decimal StopAtrMultiple = 1.5m;
    public const int TimeExitDays = 20;

    // One row per closed daily bar, aligned 1:1 with the input candles (indices match).
    public static IReadOnlyList<VtbDailyIndicators> ComputeIndicators(IReadOnlyList<NormalizedCandle> daily, int channelN)
    {
        var n = daily.Count;
        var result = new VtbDailyIndicators[n];
        for (var i = 0; i < n; i++)
        {
            var close = daily[i].Close;
            decimal sma200 = 0, atr14 = 0, hhN = 0, llN = 0;
            var hasFull = i >= 199 && i >= channelN; // needs 200 closes and N prior bars
            if (i >= 199)
            {
                decimal sum = 0;
                for (var k = i - 199; k <= i; k++) sum += daily[k].Close;
                sma200 = sum / 200m;
            }
            if (i >= 13)
            {
                decimal sum = 0;
                for (var k = i - 13; k <= i; k++) sum += daily[k].High - daily[k].Low;
                atr14 = sum / 14m;
            }
            if (i >= channelN)
            {
                // HH_N/LL_N exclude bar i itself - the window is (i-N .. i-1).
                var hi = decimal.MinValue; var lo = decimal.MaxValue;
                for (var k = i - channelN; k <= i - 1; k++) { if (daily[k].High > hi) hi = daily[k].High; if (daily[k].Low < lo) lo = daily[k].Low; }
                hhN = hi; llN = lo;
            }
            result[i] = new VtbDailyIndicators(close, sma200, atr14, hhN, llN, hasFull);
        }
        return result;
    }

    public static VtbSide? SignalAt(VtbDailyIndicators ind)
    {
        if (!ind.HasFullHistory) return null;
        if (ind.Close > ind.Sma200 && ind.Close > ind.HhN) return VtbSide.Long;
        if (ind.Close < ind.Sma200 && ind.Close < ind.LlN) return VtbSide.Short;
        return null;
    }

    // Saved positions keep their original levels even if the rolling signal history changes.
    // A forming daily bar can fill a stop/target, while a time exit requires its close.
    public static VtbClosedTrade? Settle(VtbPosition position, IReadOnlyList<NormalizedCandle> daily, DateTime asOfUtc)
    {
        foreach (var bar in daily.OrderBy(c => c.OpenTimeUtc))
        {
            if (bar.OpenTimeUtc <= position.EntryTimeUtc || bar.OpenTimeUtc > asOfUtc) continue;
            var isLong = position.Side == VtbSide.Long;
            var hitStop = isLong ? bar.Low <= position.Stop : bar.High >= position.Stop;
            var hitTarget = isLong ? bar.High >= position.Target : bar.Low <= position.Target;
            var timeExit = bar.OpenTimeUtc >= position.EntryTimeUtc.AddDays(TimeExitDays)
                && bar.IsComplete && bar.CloseTimeUtc <= asOfUtc;
            if (!hitStop && !hitTarget && !timeExit) continue;

            var reason = hitStop ? VtbExitReason.Stop : hitTarget ? VtbExitReason.Target : VtbExitReason.Time;
            var exit = reason == VtbExitReason.Stop ? position.Stop : reason == VtbExitReason.Target ? position.Target : bar.Close;
            if (hitStop && (isLong ? bar.Open < position.Stop : bar.Open > position.Stop)) exit = bar.Open;
            var r = (isLong ? 1m : -1m) * (exit - position.EntryRef) / position.Risk;
            return new VtbClosedTrade(position.Side, position.EntryTimeUtc, position.EntryRef, position.Stop,
                position.Target, position.Risk, bar.CloseTimeUtc <= asOfUtc ? bar.CloseTimeUtc : asOfUtc, exit, reason, r);
        }
        return null;
    }

    // Walks the full closed-bar history deterministically and returns every closed trade plus
    // whatever's left open at the end - same shape as the verified Python replay(). Pure
    // function of the candle history: callers diff the result against persisted state rather
    // than this function tracking "have I seen this bar before" itself.
    public static (IReadOnlyList<VtbClosedTrade> Closed, VtbPosition? Open) Replay(
        IReadOnlyList<NormalizedCandle> daily, int channelN, decimal rewardToRisk)
    {
        var ind = ComputeIndicators(daily, channelN);
        var closed = new List<VtbClosedTrade>();
        VtbPosition? pos = null;

        for (var i = 0; i < daily.Count; i++)
        {
            var bar = daily[i];
            if (pos is not null && bar.OpenTimeUtc > pos.EntryTimeUtc)
            {
                var outcome = Settle(pos, new[] { bar }, bar.CloseTimeUtc);
                if (outcome is not null)
                {
                    closed.Add(outcome);
                    pos = null;
                }
                else continue; // still open, no new entry possible today
            }

            if (pos is null)
            {
                var side = SignalAt(ind[i]);
                if (side is null) continue;
                var risk = StopAtrMultiple * ind[i].Atr14;
                if (risk <= 0) continue;
                var entry = ind[i].Close;
                var isLong = side == VtbSide.Long;
                // EntryTimeUtc anchors to this bar's OWN open time (matches the Python
                // reference's row-index comparison) - NOT its close time, which equals the
                // next bar's open time for back-to-back daily bars and would make the
                // very next bar's exit-check (`bar.OpenTimeUtc > pos.EntryTimeUtc`) false,
                // silently delaying the first possible exit by one extra day.
                pos = new VtbPosition(side.Value, bar.OpenTimeUtc, entry,
                    risk, isLong ? entry - risk : entry + risk, isLong ? entry + rewardToRisk * risk : entry - rewardToRisk * risk);
            }
        }
        return (closed, pos);
    }
}
