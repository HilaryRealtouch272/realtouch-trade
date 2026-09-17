using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum StructureEventType { BullishBos, BearishBos, BullishChoch, BearishChoch }
public enum TrendState { Unknown, Bullish, Bearish }

public record StructureEvent(
    StructureEventType Type,
    DateTime CandleTimeUtc,
    decimal ClosePrice,
    Pivot BrokenPivot
);

public record StructureResult(IReadOnlyList<StructureEvent> Events, TrendState FinalTrend);

// Section 5: BOS/CHoCH detection. Walks candles in chronological order and
// only ever references a pivot once it was actually confirmed at that point
// in time (ConfirmedAtUtc <= current candle close) - no look-ahead leakage.
// A wick alone never confirms BOS/CHoCH; only a completed candle's close.
public static class StructureAnalyzer
{
    private const decimal BosAtrMultiplier = 0.10m;

    public static StructureResult Analyze(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        var pivots = PivotDetector.DetectConfirmedPivots(allCandles, timeframe)
            .OrderBy(p => p.ConfirmedAtUtc).ToList();
        var atr = Indicators.Atr(completed, 14);

        var events = new List<StructureEvent>();
        var trend = TrendState.Unknown;
        Pivot? activeHigh = null;
        Pivot? activeLow = null;
        int pivotCursor = 0;

        for (int i = 0; i < completed.Count; i++)
        {
            var candle = completed[i];

            // Make newly-confirmed pivots available exactly when they become known.
            while (pivotCursor < pivots.Count && pivots[pivotCursor].ConfirmedAtUtc <= candle.CloseTimeUtc)
            {
                var p = pivots[pivotCursor];
                if (p.Type == PivotType.High) activeHigh = p;
                else activeLow = p;
                pivotCursor++;
            }

            var currentAtr = atr[i];
            if (currentAtr is null) continue;
            var threshold = BosAtrMultiplier * currentAtr.Value;

            if (activeHigh is not null && candle.Close > activeHigh.Price + threshold)
            {
                var eventType = trend == TrendState.Bearish ? StructureEventType.BullishChoch : StructureEventType.BullishBos;
                events.Add(new StructureEvent(eventType, candle.CloseTimeUtc, candle.Close, activeHigh));
                trend = TrendState.Bullish;
                activeHigh = null;
            }
            else if (activeLow is not null && candle.Close < activeLow.Price - threshold)
            {
                var eventType = trend == TrendState.Bullish ? StructureEventType.BearishChoch : StructureEventType.BearishBos;
                events.Add(new StructureEvent(eventType, candle.CloseTimeUtc, candle.Close, activeLow));
                trend = TrendState.Bearish;
                activeLow = null;
            }
        }

        return new StructureResult(events, trend);
    }
}
