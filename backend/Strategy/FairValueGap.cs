using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum FvgDirection { Bullish, Bearish }
public enum MitigationStatus { Unmitigated, PartiallyMitigated, FullyMitigated, Invalidated }

// Section 11: a 3-candle imbalance. Upper/Lower are always Upper > Lower.
public record FairValueGap(
    FvgDirection Direction,
    decimal Upper,
    decimal Lower,
    decimal Midpoint,
    DateTime OriginCandleTimeUtc,
    Timeframe Timeframe,
    MitigationStatus Status
);

public static class FvgDetector
{
    private const decimal MinWidthAtrMultiplier = 0.10m;

    public static IReadOnlyList<FairValueGap> Detect(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        var atr = Indicators.Atr(completed, 14);
        var gaps = new List<FairValueGap>();

        for (int i = 2; i < completed.Count; i++)
        {
            var first = completed[i - 2];
            var third = completed[i];
            var currentAtr = atr[i];
            if (currentAtr is null) continue;
            var minWidth = MinWidthAtrMultiplier * currentAtr.Value;

            if (third.Low > first.High && (third.Low - first.High) >= minWidth)
            {
                gaps.Add(new FairValueGap(FvgDirection.Bullish, third.Low, first.High, (third.Low + first.High) / 2,
                    completed[i - 1].OpenTimeUtc, timeframe, MitigationStatus.Unmitigated));
            }
            else if (third.High < first.Low && (first.Low - third.High) >= minWidth)
            {
                gaps.Add(new FairValueGap(FvgDirection.Bearish, first.Low, third.High, (first.Low + third.High) / 2,
                    completed[i - 1].OpenTimeUtc, timeframe, MitigationStatus.Unmitigated));
            }
        }

        return ApplyMitigation(gaps, completed);
    }

    // Walks candles after each gap's origin to determine mitigation status:
    // - PartiallyMitigated: price traded into the gap but not through it.
    // - FullyMitigated: price traded through the full gap.
    // - Invalidated: a completed candle closed through the gap's far (distal) boundary.
    private static IReadOnlyList<FairValueGap> ApplyMitigation(List<FairValueGap> gaps, List<NormalizedCandle> completed)
    {
        var result = new List<FairValueGap>(gaps.Count);
        foreach (var gap in gaps)
        {
            var status = MitigationStatus.Unmitigated;
            var afterOrigin = completed.Where(c => c.OpenTimeUtc > gap.OriginCandleTimeUtc);

            foreach (var c in afterOrigin)
            {
                if (gap.Direction == FvgDirection.Bullish)
                {
                    if (c.Close < gap.Lower) { status = MitigationStatus.Invalidated; break; }
                    if (c.Low <= gap.Lower) status = MitigationStatus.FullyMitigated;
                    else if (c.Low < gap.Upper && status == MitigationStatus.Unmitigated) status = MitigationStatus.PartiallyMitigated;
                }
                else
                {
                    if (c.Close > gap.Upper) { status = MitigationStatus.Invalidated; break; }
                    if (c.High >= gap.Upper) status = MitigationStatus.FullyMitigated;
                    else if (c.High > gap.Lower && status == MitigationStatus.Unmitigated) status = MitigationStatus.PartiallyMitigated;
                }
            }

            result.Add(gap with { Status = status });
        }
        return result;
    }
}
