using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public record RealtouchWillisZone(
    OrderBlockDirection Direction,
    decimal ProximalBoundary,
    decimal DistalBoundary,
    DateTime CandleTimeUtc,
    Timeframe Timeframe,
    OrderBlock OriginatingOrderBlock,
    bool OverlapsPreferredRetracement,
    bool OverlapsToleratedRetracement,
    FairValueGap? OverlappingFvg
);

// Section 14: the Realtouch Willis Zone. Built from an order block (which
// already satisfies "originates from the final opposing candle before
// confirmed displacement") that also overlaps the preferred/tolerated
// retracement pocket and overlaps an FVG. Freshness and width are checked
// here; the "followed by liquidity and structure confirmation before entry"
// requirement is an entry-time condition, enforced by the setup models that
// consume this zone - not by detection itself.
public static class WillisZoneDetector
{
    private const decimal MinWidthAtrMultiplier = 0.10m;
    private const decimal MaxWidthAtrMultiplier = 3.0m;

    public static IReadOnlyList<RealtouchWillisZone> Detect(
        IReadOnlyList<NormalizedCandle> allCandles,
        Timeframe timeframe,
        StructureResult structure,
        IReadOnlyList<OrderBlock> orderBlocks,
        IReadOnlyList<FairValueGap> fvgs)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        var atr = Indicators.Atr(completed, 14);
        var lastAtr = atr.LastOrDefault(a => a is not null);
        var zones = new List<RealtouchWillisZone>();

        foreach (var ob in orderBlocks)
        {
            if (!OrderBlockDetector.IsValid(ob)) continue;

            var width = Math.Abs(ob.ProximalBoundary - ob.DistalBoundary);
            if (lastAtr is not null)
            {
                if (width < MinWidthAtrMultiplier * lastAtr.Value) continue;
                if (width > MaxWidthAtrMultiplier * lastAtr.Value) continue;
            }

            var range = PremiumDiscount.BuildRange(allCandles, timeframe, structure with { Events = new[] { ob.ConfirmingEvent } });
            bool preferred = false, tolerated = false;
            if (range is not null)
            {
                var isBullish = ob.Direction == OrderBlockDirection.Bullish;
                var lowFrac = PremiumDiscount.RetracementFraction(range, Math.Min(ob.ProximalBoundary, ob.DistalBoundary), isBullish);
                var highFrac = PremiumDiscount.RetracementFraction(range, Math.Max(ob.ProximalBoundary, ob.DistalBoundary), isBullish);
                var lo = Math.Min(lowFrac, highFrac);
                var hi = Math.Max(lowFrac, highFrac);
                preferred = hi >= 0.62m && lo <= 0.705m;
                tolerated = hi >= 0.50m && lo <= 0.79m;
            }
            if (!tolerated) continue; // criterion 2 is mandatory

            var zoneLow = Math.Min(ob.ProximalBoundary, ob.DistalBoundary);
            var zoneHigh = Math.Max(ob.ProximalBoundary, ob.DistalBoundary);
            var overlappingFvg = fvgs.FirstOrDefault(f =>
                (ob.Direction == OrderBlockDirection.Bullish) == (f.Direction == FvgDirection.Bullish) &&
                f.Lower <= zoneHigh && f.Upper >= zoneLow);
            if (overlappingFvg is null) continue; // criterion 3 is mandatory (FVG-based; OB/S&D/key-level variants can extend this later)

            zones.Add(new RealtouchWillisZone(
                ob.Direction, ob.ProximalBoundary, ob.DistalBoundary, ob.CandleTimeUtc, timeframe,
                ob, preferred, tolerated, overlappingFvg
            ));
        }

        return zones;
    }
}
