using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Section 14: pip/point calculation from real entry and exit fill prices.
// Every value here is decimal (never double) - the same reasoning already
// established for R-multiple math in SignalLogService: financial totals
// must not carry binary floating-point rounding noise.
public record MovementResult(
    MovementUnitName Unit,
    decimal GrossUnits,
    decimal SpreadUnits,
    decimal SlippageUnits,
    decimal CommissionUnits,
    decimal NetUnits
);

public static class PipCalculator
{
    // directionMultiplier: +1 for Long, -1 for Short - matches the brief's
    // TS reference exactly (grossUnits = directionMultiplier * (exit-entry) / unitSize).
    private static int DirectionMultiplier(bool isLong) => isLong ? 1 : -1;

    public static MovementResult Compute(
        InstrumentMetadata metadata, bool isLong, decimal entryPrice, decimal exitPrice,
        decimal spreadInPriceTerms = 0m, decimal slippageInPriceTerms = 0m, decimal commissionInPriceTerms = 0m)
    {
        var unitSize = metadata.MovementUnitSize;
        var grossUnits = DirectionMultiplier(isLong) * (exitPrice - entryPrice) / unitSize;

        var spreadUnits = spreadInPriceTerms / unitSize;
        var slippageUnits = slippageInPriceTerms / unitSize;
        var commissionUnits = commissionInPriceTerms / unitSize;
        var netUnits = grossUnits - spreadUnits - slippageUnits - commissionUnits;

        return new MovementResult(metadata.MovementUnitName, grossUnits, spreadUnits, slippageUnits, commissionUnits, netUnits);
    }

    // Section 15: weighted result across partial exits. Each fill carries
    // its own position fraction and its own net movement (already costed) -
    // this never assumes TP1 being touched equals TP1 being realised;
    // callers only pass fills that actually executed.
    public static decimal WeightedNetUnits(IReadOnlyList<(decimal PositionFraction, decimal NetUnits)> fills) =>
        fills.Sum(f => f.PositionFraction * f.NetUnits);

    public static string UnitLabel(MovementUnitName unit) => unit switch
    {
        MovementUnitName.Pip => "pips",
        MovementUnitName.Point => "points",
        MovementUnitName.Tick => "ticks",
        _ => "units"
    };
}
