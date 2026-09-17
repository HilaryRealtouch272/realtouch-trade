using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum OrderBlockDirection { Bullish, Bearish }

public record OrderBlock(
    OrderBlockDirection Direction,
    decimal ProximalBoundary, // near edge, closest to price at formation
    decimal DistalBoundary,   // far edge - a close through this invalidates the block
    DateTime CandleTimeUtc,
    Timeframe Timeframe,
    StructureEvent ConfirmingEvent,
    bool ClosedThroughDistal,
    int ReactionCount
);

// Section 12: the last opposing candle before a displacement move that
// produces a confirmed BOS/CHoCH. Only valid while its distal boundary
// hasn't been closed through and it isn't past its age limit.
public static class OrderBlockDetector
{
    private static int MaxAgeBars(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => 26,
        Timeframe.Daily => 60,
        Timeframe.H4 => 120,
        Timeframe.H1 => 200,
        Timeframe.M15 => 300,
        _ => 100
    };

    public static IReadOnlyList<OrderBlock> Detect(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, StructureResult structure)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        var blocks = new List<OrderBlock>();
        var maxAge = MaxAgeBars(timeframe);

        foreach (var evt in structure.Events)
        {
            var breakIndex = completed.FindIndex(c => c.CloseTimeUtc == evt.CandleTimeUtc);
            if (breakIndex < 1) continue;

            var isBullish = evt.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch;
            // Walk backwards from the break candle to the last opposite-coloured candle.
            int obIndex = -1;
            for (int i = breakIndex; i >= Math.Max(0, breakIndex - 20); i--)
            {
                var candle = completed[i];
                var isBearishCandle = candle.Close < candle.Open;
                if (isBullish && isBearishCandle) { obIndex = i; break; }
                if (!isBullish && !isBearishCandle) { obIndex = i; break; }
            }
            if (obIndex < 0) continue;

            var obCandle = completed[obIndex];
            decimal proximal, distal;
            if (isBullish)
            {
                proximal = obCandle.Open;
                distal = obCandle.Low;
            }
            else
            {
                proximal = obCandle.Open;
                distal = obCandle.High;
            }

            var age = completed.Count - 1 - obIndex;
            if (age > maxAge) continue;

            var closedThrough = false;
            var reactions = 0;
            for (int i = obIndex + 1; i < completed.Count; i++)
            {
                var c = completed[i];
                if (isBullish)
                {
                    if (c.Close < distal) { closedThrough = true; break; }
                    if (c.Low <= proximal) reactions++;
                }
                else
                {
                    if (c.Close > distal) { closedThrough = true; break; }
                    if (c.High >= proximal) reactions++;
                }
            }

            blocks.Add(new OrderBlock(
                isBullish ? OrderBlockDirection.Bullish : OrderBlockDirection.Bearish,
                proximal, distal, obCandle.OpenTimeUtc, timeframe, evt, closedThrough, reactions
            ));
        }

        return blocks;
    }

    public static bool IsValid(OrderBlock block) => !block.ClosedThroughDistal;
}
