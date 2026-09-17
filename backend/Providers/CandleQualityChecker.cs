using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Section 3 data rules: detect duplicate, missing, out-of-order and stale
// candles so the strategy engine never confirms a signal on bad data.
public static class CandleQualityChecker
{
    // Tolerance before the most recent candle is considered stale, as a
    // multiple of the timeframe's own duration (crypto/FX feeds can lag a bit
    // around provider maintenance without truly being "stale").
    private const double StaleToleranceMultiplier = 2.5;

    public static IReadOnlyList<NormalizedCandle> Annotate(IReadOnlyList<NormalizedCandle> raw, Timeframe timeframe, DateTime nowUtc)
    {
        if (raw.Count == 0) return raw;

        var ordered = raw.OrderBy(c => c.OpenTimeUtc).ToList();
        var result = new List<NormalizedCandle>(ordered.Count);
        var expectedStep = TimeframeConfig.Duration(timeframe);
        DateTime? previousOpen = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            var quality = DataQuality.Ok;

            if (previousOpen.HasValue)
            {
                if (c.OpenTimeUtc == previousOpen.Value)
                {
                    quality = DataQuality.Duplicate;
                }
                else if (c.OpenTimeUtc < previousOpen.Value)
                {
                    quality = DataQuality.OutOfOrder;
                }
                else if (c.OpenTimeUtc - previousOpen.Value > expectedStep + expectedStep / 2)
                {
                    quality = DataQuality.Gap;
                }
            }

            if (quality == DataQuality.Duplicate) continue; // drop true duplicates outright

            result.Add(c with { Quality = quality });
            previousOpen = c.OpenTimeUtc;
        }

        // Mark staleness on the most recent candle only, relative to "now".
        if (result.Count > 0)
        {
            var last = result[^1];
            var staleAfter = TimeSpan.FromTicks((long)(expectedStep.Ticks * StaleToleranceMultiplier));
            if (nowUtc - last.CloseTimeUtc > staleAfter && last.Quality == DataQuality.Ok)
            {
                result[^1] = last with { Quality = DataQuality.Stale };
            }
        }

        return result;
    }

    public static bool HasUsableData(IReadOnlyList<NormalizedCandle> candles, int minimumCompleted)
    {
        var completedOk = candles.Count(c => c.IsComplete && c.Quality == DataQuality.Ok);
        return completedOk >= minimumCompleted && candles[^1].Quality != DataQuality.Stale;
    }
}
