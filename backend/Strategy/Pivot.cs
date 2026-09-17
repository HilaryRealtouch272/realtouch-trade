using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum PivotType { High, Low }

// A confirmed swing point. ConfirmedAtUtc is when the required right-side
// candles closed - the pivot must never be used by the strategy before this
// timestamp (section 5: non-repainting).
public record Pivot(int Index, DateTime CandleTimeUtc, decimal Price, PivotType Type, DateTime ConfirmedAtUtc);

public static class PivotDetector
{
    public static IReadOnlyList<Pivot> DetectConfirmedPivots(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe)
    {
        var bars = TimeframeConfig.PivotBars(timeframe);
        var completed = allCandles.Where(c => c.IsComplete && c.Quality != DataQuality.Duplicate).OrderBy(c => c.OpenTimeUtc).ToList();
        var pivots = new List<Pivot>();

        for (int i = bars; i < completed.Count - bars; i++)
        {
            var candidate = completed[i];
            bool isHigh = true, isLow = true;

            for (int j = i - bars; j <= i + bars; j++)
            {
                if (j == i) continue;
                if (completed[j].High >= candidate.High) isHigh = false;
                if (completed[j].Low <= candidate.Low) isLow = false;
                if (!isHigh && !isLow) break;
            }

            // The pivot becomes usable only once the right-side confirmation candles have closed.
            var confirmedAt = completed[i + bars].CloseTimeUtc;
            if (isHigh) pivots.Add(new Pivot(i, candidate.OpenTimeUtc, candidate.High, PivotType.High, confirmedAt));
            if (isLow) pivots.Add(new Pivot(i, candidate.OpenTimeUtc, candidate.Low, PivotType.Low, confirmedAt));
        }

        return pivots;
    }
}
