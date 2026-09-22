using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class PipCalculatorTests
{
    [Fact]
    public void LongGbpUsdGrossPipsMatchesExitMinusEntryOverPipSize()
    {
        var meta = InstrumentMetadataCatalog.For("GBP/USD");

        var result = PipCalculator.Compute(meta, isLong: true, entryPrice: 1.30000m, exitPrice: 1.30500m);

        // (1.30500 - 1.30000) / 0.0001 = 50 pips
        Assert.Equal(50m, result.GrossUnits);
    }

    [Fact]
    public void ShortGbpUsdGrossPipsIsEntryMinusExitOverPipSize()
    {
        var meta = InstrumentMetadataCatalog.For("GBP/USD");

        var result = PipCalculator.Compute(meta, isLong: false, entryPrice: 1.30500m, exitPrice: 1.30000m);

        // Short profits when price falls: (1.30500 - 1.30000)/0.0001 = 50 pips, per the brief's short formula.
        Assert.Equal(50m, result.GrossUnits);
    }

    [Fact]
    public void GbpJpyUsesTheSecondDecimalAsAFullPipNotTheFourth()
    {
        // Section 13: JPY pairs' pip is 0.01, not 0.0001 - a real distinction
        // the brief is explicit about (a fifth-decimal move on non-JPY, or
        // third-decimal on JPY, is a pipette, not a full pip).
        var meta = InstrumentMetadataCatalog.For("GBP/JPY");
        Assert.Equal(0.01m, meta.MovementUnitSize);

        var result = PipCalculator.Compute(meta, isLong: true, entryPrice: 190.00m, exitPrice: 190.50m);

        // (190.50 - 190.00) / 0.01 = 50 pips
        Assert.Equal(50m, result.GrossUnits);
    }

    [Fact]
    public void NetUnitsSubtractsSpreadSlippageAndCommission()
    {
        var meta = InstrumentMetadataCatalog.For("EUR/USD");

        // 30 gross pips, minus 1.5 spread pips, 0.5 slippage pips, 0.3 commission pips.
        var result = PipCalculator.Compute(meta, isLong: true, entryPrice: 1.10000m, exitPrice: 1.10300m,
            spreadInPriceTerms: 0.00015m, slippageInPriceTerms: 0.00005m, commissionInPriceTerms: 0.00003m);

        Assert.Equal(30m, result.GrossUnits);
        Assert.Equal(27.7m, result.NetUnits); // 30 - 1.5 - 0.5 - 0.3
    }

    [Fact]
    public void CryptoMovementIsReportedInQuoteCurrencyPointsNotPips()
    {
        // Section 13: "Do not call Bitcoin dollar movement pips."
        var meta = InstrumentMetadataCatalog.For("BTC/USDT");
        Assert.Equal(MovementUnitName.Point, meta.MovementUnitName);

        var result = PipCalculator.Compute(meta, isLong: true, entryPrice: 60000m, exitPrice: 60500m);

        Assert.Equal(500m, result.GrossUnits); // the raw dollar move, since MovementUnitSize is 1
    }

    [Fact]
    public void WeightedNetUnitsMatchesTheBriefsPartialExitExample()
    {
        // Section 15's worked example: 25% closed at TP1, 75% later closed
        // at stop. Trade Net Pips = (0.25 x TP1 pips) + (0.75 x Stop pips).
        var fills = new (decimal, decimal)[] { (0.25m, 20m), (0.75m, -10m) };

        var weighted = PipCalculator.WeightedNetUnits(fills);

        Assert.Equal(-2.5m, weighted); // 0.25*20 + 0.75*(-10) = 5 - 7.5 = -2.5
    }

    [Fact]
    public void UnconfiguredSymbolThrowsRatherThanGuessingAUnitSize()
    {
        Assert.Throws<KeyNotFoundException>(() => InstrumentMetadataCatalog.For("NOT/CONFIGURED"));
    }
}
