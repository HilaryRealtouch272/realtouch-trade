using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum KeyLevelType
{
    PreviousWeekHigh, PreviousWeekLow, PreviousDayHigh, PreviousDayLow,
    ConfirmedSwingHigh, ConfirmedSwingLow, EqualHighs, EqualLows,
    RangeHigh, RangeLow, RangeEquilibrium
}

public record KeyLevel(
    KeyLevelType Type,
    Timeframe Timeframe,
    decimal Upper,
    decimal Lower,
    DateTime OriginatingCandleTimeUtc,
    DateTime CreatedAtUtc,
    bool Mitigated,
    bool Invalidated,
    int ReactionCount
);

// Section 8: a discrete, stored catalog of key levels - not just raw pivots.
// Session highs/lows (Asian/London/NY) are NOT included here yet: they need
// per-candle UTC-hour bucketing plus proper IANA/DST handling to be accurate,
// which is a separate, not-yet-built piece (see STRATEGY.md limitations).
public static class KeyLevelCatalog
{
    private const decimal EqualLevelTolerancePercent = 0.0015m; // 0.15% - "approximately equal" tolerance

    public static IReadOnlyList<KeyLevel> Build(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        var levels = new List<KeyLevel>();
        var now = DateTime.UtcNow;

        levels.AddRange(BuildPreviousPeriodLevels(completed, timeframe, now));
        levels.AddRange(BuildSwingLevels(allCandles, timeframe, completed, now));
        levels.AddRange(BuildEqualLevels(allCandles, timeframe, now));
        levels.AddRange(BuildRangeLevels(completed, timeframe, now));

        return levels;
    }

    private static IEnumerable<KeyLevel> BuildPreviousPeriodLevels(List<NormalizedCandle> completed, Timeframe timeframe, DateTime now)
    {
        // "Previous week/day" only makes sense when this series' own timeframe
        // IS that period (fetching separate Weekly/Daily context series for
        // every other timeframe is section 7's multi-timeframe fetch, not yet wired).
        if (completed.Count < 2) yield break;
        if (timeframe == Timeframe.Daily)
        {
            var prev = completed[^2];
            yield return new KeyLevel(KeyLevelType.PreviousDayHigh, timeframe, prev.High, prev.High, prev.OpenTimeUtc, now, false, false, 0);
            yield return new KeyLevel(KeyLevelType.PreviousDayLow, timeframe, prev.Low, prev.Low, prev.OpenTimeUtc, now, false, false, 0);
        }
        else if (timeframe == Timeframe.Weekly)
        {
            var prev = completed[^2];
            yield return new KeyLevel(KeyLevelType.PreviousWeekHigh, timeframe, prev.High, prev.High, prev.OpenTimeUtc, now, false, false, 0);
            yield return new KeyLevel(KeyLevelType.PreviousWeekLow, timeframe, prev.Low, prev.Low, prev.OpenTimeUtc, now, false, false, 0);
        }
    }

    private static IEnumerable<KeyLevel> BuildSwingLevels(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, List<NormalizedCandle> completed, DateTime now)
    {
        var pivots = PivotDetector.DetectConfirmedPivots(allCandles, timeframe);
        var lastClose = completed.Count > 0 ? completed[^1].Close : (decimal?)null;

        foreach (var pivot in pivots)
        {
            var type = pivot.Type == PivotType.High ? KeyLevelType.ConfirmedSwingHigh : KeyLevelType.ConfirmedSwingLow;
            var invalidated = lastClose is not null && (
                (pivot.Type == PivotType.High && lastClose > pivot.Price) ||
                (pivot.Type == PivotType.Low && lastClose < pivot.Price));
            var reactions = completed.Count(c =>
                pivot.Type == PivotType.High ? Math.Abs(c.High - pivot.Price) / pivot.Price <= EqualLevelTolerancePercent
                                              : Math.Abs(c.Low - pivot.Price) / pivot.Price <= EqualLevelTolerancePercent);
            yield return new KeyLevel(type, timeframe, pivot.Price, pivot.Price, pivot.CandleTimeUtc, pivot.ConfirmedAtUtc, false, invalidated, reactions);
        }
    }

    // Equal highs/lows: two or more confirmed pivots of the same type within tolerance of each other.
    private static IEnumerable<KeyLevel> BuildEqualLevels(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, DateTime now)
    {
        var pivots = PivotDetector.DetectConfirmedPivots(allCandles, timeframe);
        foreach (var group in pivots.GroupBy(p => p.Type))
        {
            var sorted = group.OrderBy(p => p.Price).ToList();
            var cluster = new List<Pivot>();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (cluster.Count == 0) { cluster.Add(sorted[i]); continue; }
                var avg = cluster.Average(p => p.Price);
                if (Math.Abs(sorted[i].Price - avg) / avg <= EqualLevelTolerancePercent)
                {
                    cluster.Add(sorted[i]);
                }
                else
                {
                    if (cluster.Count >= 2)
                        yield return EmitEqualLevel(cluster, group.Key, timeframe, now);
                    cluster = new List<Pivot> { sorted[i] };
                }
            }
            if (cluster.Count >= 2)
                yield return EmitEqualLevel(cluster, group.Key, timeframe, now);
        }
    }

    private static KeyLevel EmitEqualLevel(List<Pivot> cluster, PivotType type, Timeframe timeframe, DateTime now)
    {
        var prices = cluster.Select(p => p.Price).ToList();
        var latest = cluster.OrderByDescending(p => p.CandleTimeUtc).First();
        return new KeyLevel(
            type == PivotType.High ? KeyLevelType.EqualHighs : KeyLevelType.EqualLows,
            timeframe, prices.Max(), prices.Min(), latest.CandleTimeUtc, now, false, false, cluster.Count
        );
    }

    private static IEnumerable<KeyLevel> BuildRangeLevels(List<NormalizedCandle> completed, Timeframe timeframe, DateTime now)
    {
        const int lookback = 20;
        if (completed.Count < lookback) yield break;
        var window = completed.Skip(completed.Count - lookback).ToList();
        var high = window.Max(c => c.High);
        var low = window.Min(c => c.Low);
        var eq = (high + low) / 2;
        var originTime = window[0].OpenTimeUtc;
        yield return new KeyLevel(KeyLevelType.RangeHigh, timeframe, high, high, originTime, now, false, false, 0);
        yield return new KeyLevel(KeyLevelType.RangeLow, timeframe, low, low, originTime, now, false, false, 0);
        yield return new KeyLevel(KeyLevelType.RangeEquilibrium, timeframe, eq, eq, originTime, now, false, false, 0);
    }
}
