using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Section 10: a displacement candle is unusually large, directional, and
// contributes to BOS/CHoCH. Used to validate order blocks and the Willis
// Zone - never used on its own to generate a signal (section 4).
public static class Displacement
{
    private const decimal MinBodyToMedianRatio = 1.5m;
    private const decimal MinBodyToRangeRatio = 0.65m;

    // Volume is only ever used where it is genuinely reliable (section 10): every
    // one of the last 21 completed candles must carry a positive volume. FX
    // feeds usually have none, or only tick counts of unclear meaning, and
    // then it is simply not used - never guessed or defaulted.
    public static bool HasUsableVolume(IReadOnlyList<NormalizedCandle> completed, int lookback = 21) =>
        completed.Count >= lookback && completed.Skip(completed.Count - lookback).All(c => c.Volume is > 0);

    // The latest completed candle's volume is at least 1.5x the median of the
    // 20 candles before it.
    public static bool HasVolumeExpansion(IReadOnlyList<NormalizedCandle> completed, decimal ratio = 1.5m)
    {
        if (!HasUsableVolume(completed)) return false;
        var last = completed[^1].Volume!.Value;
        var prior = completed.Skip(completed.Count - 21).Take(20).Select(c => c.Volume!.Value).OrderBy(v => v).ToList();
        var median = (prior[9] + prior[10]) / 2;
        return median > 0 && last >= ratio * median;
    }

    public static bool IsDisplacementCandle(IReadOnlyList<NormalizedCandle> completedCandles, int index)
    {
        if (index < 20 || index >= completedCandles.Count) return false;

        var candle = completedCandles[index];
        var body = Math.Abs(candle.Close - candle.Open);
        var range = candle.High - candle.Low;
        if (range == 0) return false;

        var priorBodies = new List<decimal>();
        for (int i = index - 20; i < index; i++)
            priorBodies.Add(Math.Abs(completedCandles[i].Close - completedCandles[i].Open));
        priorBodies.Sort();
        var median = priorBodies.Count % 2 == 0
            ? (priorBodies[priorBodies.Count / 2 - 1] + priorBodies[priorBodies.Count / 2]) / 2
            : priorBodies[priorBodies.Count / 2];

        if (median == 0) return false;

        var bodyRatio = body / range;
        var isBullish = candle.Close > candle.Open;
        // "Close near the directional end of the candle": upper third for a
        // bullish candle, lower third for a bearish one.
        var closeNearEnd = isBullish
            ? (candle.High - candle.Close) <= range * 0.33m
            : (candle.Close - candle.Low) <= range * 0.33m;

        return body >= median * MinBodyToMedianRatio && bodyRatio >= MinBodyToRangeRatio && closeNearEnd;
    }
}
