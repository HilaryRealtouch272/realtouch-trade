using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Shared indicator math used by structure/condition classification. Indicators
// support the strategy (section 4: "no indicator should create a signal by
// itself") - they are never the sole basis for a signal.
public static class Indicators
{
    public static decimal[] Ema(IReadOnlyList<NormalizedCandle> candles, int length)
    {
        var result = new decimal[candles.Count];
        if (candles.Count == 0) return result;
        var k = 2m / (length + 1);
        result[0] = candles[0].Close;
        for (int i = 1; i < candles.Count; i++)
            result[i] = candles[i].Close * k + result[i - 1] * (1 - k);
        return result;
    }

    // Wilder's ATR - each value uses only candles up to and including that index (causal).
    public static decimal?[] Atr(IReadOnlyList<NormalizedCandle> candles, int length)
    {
        var result = new decimal?[candles.Count];
        if (candles.Count < 2) return result;

        var trueRanges = new decimal[candles.Count];
        for (int i = 1; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            trueRanges[i] = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
        }

        decimal sum = 0;
        for (int i = 1; i <= length && i < candles.Count; i++) sum += trueRanges[i];
        if (length >= candles.Count) return result;

        // Rounded at each step: decimal division by `length` doesn't terminate
        // cleanly, and left unrounded this compounds over hundreds of candles
        // into 20+ significant digits of noise that then leaks into every
        // downstream stop/target price and its user-facing description text.
        var atr = Math.Round(sum / length, 10);
        result[length] = atr;
        for (int i = length + 1; i < candles.Count; i++)
        {
            atr = Math.Round((atr * (length - 1) + trueRanges[i]) / length, 10);
            result[i] = atr;
        }
        return result;
    }

    // Wilder's ADX(14). Values are null during warm-up (causal - no look-ahead).
    public static decimal?[] Adx(IReadOnlyList<NormalizedCandle> candles, int length = 14)
    {
        int n = candles.Count;
        var result = new decimal?[n];
        if (n < length * 2 + 1) return result;

        var plusDm = new decimal[n];
        var minusDm = new decimal[n];
        var tr = new decimal[n];

        for (int i = 1; i < n; i++)
        {
            var upMove = candles[i].High - candles[i - 1].High;
            var downMove = candles[i - 1].Low - candles[i].Low;
            plusDm[i] = (upMove > downMove && upMove > 0) ? upMove : 0;
            minusDm[i] = (downMove > upMove && downMove > 0) ? downMove : 0;
            var prevClose = candles[i - 1].Close;
            tr[i] = Math.Max(candles[i].High - candles[i].Low, Math.Max(Math.Abs(candles[i].High - prevClose), Math.Abs(candles[i].Low - prevClose)));
        }

        decimal smoothedTr = 0, smoothedPlusDm = 0, smoothedMinusDm = 0;
        for (int i = 1; i <= length; i++) { smoothedTr += tr[i]; smoothedPlusDm += plusDm[i]; smoothedMinusDm += minusDm[i]; }

        var dxValues = new List<decimal>();
        decimal? adx = null;

        for (int i = length + 1; i < n; i++)
        {
            smoothedTr = smoothedTr - (smoothedTr / length) + tr[i];
            smoothedPlusDm = smoothedPlusDm - (smoothedPlusDm / length) + plusDm[i];
            smoothedMinusDm = smoothedMinusDm - (smoothedMinusDm / length) + minusDm[i];

            var plusDi = smoothedTr == 0 ? 0 : 100 * smoothedPlusDm / smoothedTr;
            var minusDi = smoothedTr == 0 ? 0 : 100 * smoothedMinusDm / smoothedTr;
            var diSum = plusDi + minusDi;
            var dx = diSum == 0 ? 0 : 100 * Math.Abs(plusDi - minusDi) / diSum;
            dxValues.Add(dx);

            if (dxValues.Count == length)
            {
                adx = dxValues.Average();
                result[i] = adx;
            }
            else if (dxValues.Count > length)
            {
                adx = ((adx!.Value * (length - 1)) + dx) / length;
                result[i] = adx;
            }
        }

        return result;
    }
}
