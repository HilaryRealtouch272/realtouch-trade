using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Tests;

public static class CandleFixtures
{
    public static List<NormalizedCandle> FromOhlc(
        IEnumerable<(decimal open, decimal high, decimal low, decimal close)> bars,
        Timeframe tf = Timeframe.Daily,
        DateTime? startUtc = null,
        bool lastIsIncomplete = false)
    {
        var start = startUtc ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var duration = TimeframeConfig.Duration(tf);
        var list = bars.ToList();
        var result = new List<NormalizedCandle>(list.Count);
        var receivedAt = DateTime.UtcNow;

        for (int i = 0; i < list.Count; i++)
        {
            var (open, high, low, close) = list[i];
            var openTime = start + TimeSpan.FromTicks(duration.Ticks * i);
            var isComplete = !(lastIsIncomplete && i == list.Count - 1);
            result.Add(new NormalizedCandle(
                "TEST/USD", "TESTUSD", "Fixture", tf,
                openTime, openTime + duration,
                open, high, low, close, null,
                isComplete, receivedAt, DataQuality.Ok
            ));
        }
        return result;
    }

    // A zig-zag with genuine local turning points: each leg moves price
    // monotonically toward the next extreme, and only the turning-point bar
    // itself gets wick padding - so it is strictly the local high/low with
    // every neighbour on both sides, letting real pivots/BOS form. Overall
    // drift is upward (each swing high/low exceeds the last).
    public static List<(decimal open, decimal high, decimal low, decimal close)> UptrendBars(int count, decimal start = 100m, decimal step = 1.2m, int legLength = 6)
        => ZigZag(count, start, legLength, firstLegNet: step * 3m, secondLegNet: -step);

    public static List<(decimal open, decimal high, decimal low, decimal close)> DowntrendBars(int count, decimal start = 100m, decimal step = 1.2m, int legLength = 6)
        => ZigZag(count, start, legLength, firstLegNet: -step * 3m, secondLegNet: step);

    // Alternates two leg types (net price movement per leg given explicitly,
    // signed) so overall drift matches (firstLegNet + secondLegNet) per cycle.
    // Only the last bar of each leg gets wick padding in the leg's direction,
    // making it a clean local extreme relative to every neighbouring bar.
    private static List<(decimal open, decimal high, decimal low, decimal close)> ZigZag(
        int count, decimal start, int legLength, decimal firstLegNet, decimal secondLegNet)
    {
        var bars = new List<(decimal, decimal, decimal, decimal)>();
        var price = start;
        var padding = Math.Max(Math.Abs(firstLegNet), Math.Abs(secondLegNet)) * 0.5m;
        var useFirst = true;

        while (bars.Count < count)
        {
            var legNet = useFirst ? firstLegNet : secondLegNet;
            for (int i = 0; i < legLength && bars.Count < count; i++)
            {
                var open = price;
                var close = price + legNet / legLength;
                var isTurningPoint = i == legLength - 1;
                decimal high, low;
                if (isTurningPoint)
                {
                    high = Math.Max(open, close) + (legNet > 0 ? padding : 0);
                    low = Math.Min(open, close) - (legNet < 0 ? padding : 0);
                }
                else
                {
                    high = Math.Max(open, close);
                    low = Math.Min(open, close);
                }
                bars.Add((open, high, low, close));
                price = close;
            }
            useFirst = !useFirst;
        }
        return bars;
    }

    // Oscillates within a fixed band - no sustained directional structure.
    public static List<(decimal open, decimal high, decimal low, decimal close)> RangingBars(int count, decimal mid = 100m, decimal amplitude = 3m)
    {
        var bars = new List<(decimal, decimal, decimal, decimal)>();
        for (int i = 0; i < count; i++)
        {
            var phase = Math.Sin(i * Math.PI / 6.0);
            var close = mid + (decimal)phase * amplitude;
            var open = i == 0 ? mid : bars[i - 1].Item4;
            var high = Math.Max(open, close) + 0.4m;
            var low = Math.Min(open, close) - 0.4m;
            bars.Add((open, high, low, close));
        }
        return bars;
    }
}
