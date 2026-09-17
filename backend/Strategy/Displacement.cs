using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Section 10: a displacement candle is unusually large, directional, and
// contributes to BOS/CHoCH. Used to validate order blocks and the Willis
// Zone - never used on its own to generate a signal (section 4).
public static class Displacement
{
    private const decimal MinBodyToMedianRatio = 1.5m;
    private const decimal MinBodyToRangeRatio = 0.65m;

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
