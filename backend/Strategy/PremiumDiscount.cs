using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum PriceZone { Discount, Equilibrium, Premium }

public record DealingRange(decimal Low, decimal High, decimal Equilibrium, DateTime LowTimeUtc, DateTime HighTimeUtc);

// Section 13: dealing range built from the most recent confirmed impulse
// (the pivots either side of the latest structure event), with the
// preferred 62-70.5% retracement pocket and the wider 50-79% tolerance band.
public static class PremiumDiscount
{
    public static DealingRange? BuildRange(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, StructureResult structure)
    {
        var lastEvent = structure.Events.LastOrDefault();
        if (lastEvent is null) return null;

        var pivots = PivotDetector.DetectConfirmedPivots(allCandles, timeframe);
        var isBullish = lastEvent.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch;

        // The impulse low->high (bullish) or high->low (bearish) framing the break.
        var opposite = pivots.Where(p => p.Type == (isBullish ? PivotType.Low : PivotType.High) && p.CandleTimeUtc <= lastEvent.CandleTimeUtc)
            .OrderByDescending(p => p.CandleTimeUtc).FirstOrDefault();
        if (opposite is null) return null;

        var low = isBullish ? opposite.Price : lastEvent.ClosePrice;
        var high = isBullish ? lastEvent.ClosePrice : opposite.Price;
        if (high <= low) return null;

        var equilibrium = (low + high) / 2;
        var lowTime = isBullish ? opposite.CandleTimeUtc : lastEvent.CandleTimeUtc;
        var highTime = isBullish ? lastEvent.CandleTimeUtc : opposite.CandleTimeUtc;
        return new DealingRange(low, high, equilibrium, lowTime, highTime);
    }

    public static PriceZone Classify(DealingRange range, decimal price)
    {
        if (price < range.Equilibrium) return PriceZone.Discount;
        if (price > range.Equilibrium) return PriceZone.Premium;
        return PriceZone.Equilibrium;
    }

    // Retracement fraction from the range's originating extreme: 0 = origin, 1 = impulse extreme.
    public static decimal RetracementFraction(DealingRange range, decimal price, bool measuringFromLow)
    {
        var span = range.High - range.Low;
        if (span == 0) return 0;
        return measuringFromLow ? (range.High - price) / span : (price - range.Low) / span;
    }

    public static bool IsInPreferredRetracementZone(decimal fraction) => fraction is >= 0.62m and <= 0.705m;
    public static bool IsInToleratedRetracementZone(decimal fraction) => fraction is >= 0.50m and <= 0.79m;
}
