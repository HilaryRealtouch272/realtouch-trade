using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Services;

public record LiveSignal(
    decimal Price, decimal Entry, decimal Stop, decimal Target, decimal Rr,
    string Direction, string Condition, int Score,
    decimal Ema20, decimal Ema50, decimal Atr14, bool BreakoutUp, bool BreakoutDown
);

// Heuristic v1: EMA20/EMA50 trend filter + ATR-based risk, fixed 2R target,
// 20-bar range breakout check. This has NOT been backtested - treat the
// resulting score/condition as a rough live read, not a validated edge.
// Ported 1:1 from the original client-side app.js implementation.
public static class SignalEngine
{
    public static decimal Ema(IReadOnlyList<decimal> values, int length)
    {
        decimal k = 2m / (length + 1);
        decimal prev = values[0];
        for (int i = 1; i < values.Count; i++)
            prev = values[i] * k + prev * (1 - k);
        return prev;
    }

    public static decimal Atr(IReadOnlyList<Candle> candles, int length)
    {
        var trueRanges = new List<decimal>();
        for (int i = 1; i < candles.Count; i++)
        {
            var c = candles[i];
            var prevClose = candles[i - 1].Close;
            var tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
            trueRanges.Add(tr);
        }
        var recent = trueRanges.Skip(Math.Max(0, trueRanges.Count - length)).ToList();
        return recent.Count == 0 ? 0 : recent.Average();
    }

    public static LiveSignal Compute(IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(c => c.Close).ToList();
        var ema20Input = closes.Skip(Math.Max(0, closes.Count - 60)).ToList();
        var ema50Input = ema20Input;
        var ema20 = Ema(ema20Input, 20);
        var ema50 = Ema(ema50Input, 50);
        var atrInput = candles.Skip(Math.Max(0, candles.Count - 30)).ToList();
        var atr14 = Atr(atrInput, 14);
        var price = closes[^1];
        var trendStrength = atr14 != 0 ? (ema20 - ema50) / atr14 : 0m;

        var rangeSlice = candles.Skip(Math.Max(0, candles.Count - 21)).Take(20).ToList();
        var rangeHigh = rangeSlice.Count > 0 ? rangeSlice.Max(c => c.High) : price;
        var rangeLow = rangeSlice.Count > 0 ? rangeSlice.Min(c => c.Low) : price;
        var breakoutUp = price > rangeHigh;
        var breakoutDown = price < rangeLow;

        var direction = trendStrength > 0.3m ? "Long" : trendStrength < -0.3m ? "Short" : "Neutral";
        var condition = (breakoutUp || breakoutDown) ? "Breakout" : Math.Abs(trendStrength) > 0.6m ? "Trending" : "Ranging";

        var trendComponent = Math.Max(-25m, Math.Min(25m, trendStrength * 22m));
        var scoreRaw = 50m + trendComponent + (breakoutUp || breakoutDown ? 10m : 0m);
        var score = (int)Math.Round(Math.Min(96m, Math.Max(8m, scoreRaw)));

        var riskSign = direction == "Short" ? -1m : 1m;
        var stopDistance = 1.5m * atr14;
        var entry = price;
        var stop = entry - riskSign * stopDistance;
        var rr = 2m;
        var target = entry + riskSign * stopDistance * rr;

        return new LiveSignal(price, entry, stop, target, rr, direction, condition, score, ema20, ema50, atr14, breakoutUp, breakoutDown);
    }

    public static string[] BuildConfluences(LiveSignal s, string timeframe)
    {
        return new[]
        {
            $"EMA20 {(s.Ema20 > s.Ema50 ? "above" : "below")} EMA50 on {timeframe} closes",
            $"ATR(14): {s.Atr14:G3} (live volatility read)",
            s.BreakoutUp ? "Price broke above the prior 20-bar range high"
                : s.BreakoutDown ? "Price broke below the prior 20-bar range low"
                : "Price is inside the prior 20-bar range",
            $"Fixed {s.Rr}R target from ATR-based stop distance"
        };
    }

    public static string BuildReasoning(LiveSignal s, string source, string timeframe)
    {
        return $"Live-computed from {source} {timeframe} candles: EMA20/EMA50 trend filter puts this {s.Direction.ToLowerInvariant()}, " +
               $"condition read as {s.Condition.ToLowerInvariant()}. This is an unvalidated v1 heuristic - it has not been backtested, " +
               "so the score is a rough live read, not a proven edge.";
    }
}
