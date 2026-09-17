using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum SweepDirection { Bullish, Bearish }

public record LiquiditySweep(
    SweepDirection Direction,
    decimal SweptLevel,
    DateTime SweepCandleTimeUtc,
    Timeframe Timeframe,
    Pivot SweptPivot
);

// Section 9: price must trade beyond a confirmed pivot, the completed candle
// must close back on the correct side of it, and displacement or a CHoCH
// must follow within the confirmation window - otherwise it is a breakout
// attempt, not a confirmed sweep, and is not returned at all.
public static class LiquiditySweepDetector
{
    private const decimal MinExcursionAtrMultiplier = 0.05m;
    private const int ConfirmationWindowBars = 5;

    public static IReadOnlyList<LiquiditySweep> Detect(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, StructureResult structure)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        var pivots = PivotDetector.DetectConfirmedPivots(allCandles, timeframe).OrderBy(p => p.ConfirmedAtUtc).ToList();
        var atr = Indicators.Atr(completed, 14);
        var sweeps = new List<LiquiditySweep>();
        var usedPivots = new HashSet<Pivot>();

        for (int i = 0; i < completed.Count; i++)
        {
            var candle = completed[i];
            var currentAtr = atr[i];
            if (currentAtr is null) continue;
            var minExcursion = MinExcursionAtrMultiplier * currentAtr.Value;

            var knownPivots = pivots.Where(p => p.ConfirmedAtUtc <= candle.CloseTimeUtc && !usedPivots.Contains(p));

            foreach (var pivot in knownPivots)
            {
                bool sweptCondition;
                SweepDirection direction;

                if (pivot.Type == PivotType.Low)
                {
                    sweptCondition = candle.Low < pivot.Price - minExcursion && candle.Close > pivot.Price;
                    direction = SweepDirection.Bullish;
                }
                else
                {
                    sweptCondition = candle.High > pivot.Price + minExcursion && candle.Close < pivot.Price;
                    direction = SweepDirection.Bearish;
                }
                if (!sweptCondition) continue;

                // Require displacement or a matching-direction structure event within the confirmation window.
                var confirmed = false;
                for (int j = i; j < Math.Min(completed.Count, i + 1 + ConfirmationWindowBars); j++)
                {
                    if (Displacement.IsDisplacementCandle(completed, j) &&
                        ((direction == SweepDirection.Bullish && completed[j].Close > completed[j].Open) ||
                         (direction == SweepDirection.Bearish && completed[j].Close < completed[j].Open)))
                    {
                        confirmed = true; break;
                    }
                    var matchingEvent = structure.Events.FirstOrDefault(e => e.CandleTimeUtc == completed[j].CloseTimeUtc &&
                        ((direction == SweepDirection.Bullish && e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch) ||
                         (direction == SweepDirection.Bearish && e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch)));
                    if (matchingEvent is not null) { confirmed = true; break; }
                }

                if (confirmed)
                {
                    sweeps.Add(new LiquiditySweep(direction, pivot.Price, candle.CloseTimeUtc, timeframe, pivot));
                    usedPivots.Add(pivot);
                }
            }
        }

        return sweeps;
    }
}
