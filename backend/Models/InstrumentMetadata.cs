namespace RealtouchSmartTrade.Api.Models;

// Section 13: central movement-unit configuration, one row per tradeable
// symbol. Real distinction the brief is explicit about: a fifth decimal
// place on non-JPY FX (or third on JPY pairs) is a pipette/fractional pip,
// not a full pip - MovementUnitSize is the size of ONE full unit in price
// terms, not "the smallest price increment the feed reports".
public enum MovementUnitName { Pip, Point, Tick }

// DefaultSpreadUnits/DefaultSlippageUnits: real, typical retail-broker
// costs in the instrument's OWN movement unit (pips/points), used to seed
// paper-trade cost estimates when the actual scan didn't capture a live
// bid/ask spread. Honest approximations, not measured live spreads - kept
// deliberately modest and documented per instrument rather than one
// generic number applied everywhere.
public record InstrumentMetadata(
    string Symbol,
    string AssetClass, // "fx" | "metal" | "energy" | "crypto"
    int PriceDecimals,
    decimal TickSize,
    MovementUnitName MovementUnitName,
    decimal MovementUnitSize,
    string QuoteCurrency,
    decimal? ContractSize = null,
    decimal DefaultSpreadUnits = 1.5m,
    decimal DefaultSlippageUnits = 0.5m,
    // Broker commission per round trip, in the same movement units. Zero for the
    // paper account (none is charged); configure per instrument if that changes.
    decimal CommissionUnits = 0m
);

public static class InstrumentMetadataCatalog
{
    // Kept in sync with SetupDefinition.cs's real instrument list. A symbol
    // missing here is a real configuration gap, not silently assumed -
    // PipCalculator.For() throws rather than guessing a unit size.
    private static readonly IReadOnlyList<InstrumentMetadata> All = new[]
    {
        new InstrumentMetadata("EUR/USD", "fx", 4, 0.00001m, MovementUnitName.Pip, 0.0001m, "USD", DefaultSpreadUnits: 1.0m, DefaultSlippageUnits: 0.3m),
        new InstrumentMetadata("GBP/USD", "fx", 4, 0.00001m, MovementUnitName.Pip, 0.0001m, "USD", DefaultSpreadUnits: 1.2m, DefaultSlippageUnits: 0.3m),
        // JPY-quoted pairs: 2 display decimals, pip is the SECOND decimal
        // (0.01), not the fourth - a pipette here is the third decimal.
        // GBP/JPY is a wider cross than the majors above - real typical
        // retail spread reflects that.
        new InstrumentMetadata("GBP/JPY", "fx", 2, 0.001m, MovementUnitName.Pip, 0.01m, "JPY", DefaultSpreadUnits: 2.5m, DefaultSlippageUnits: 0.7m),
        new InstrumentMetadata("XAU/USD", "metal", 1, 0.01m, MovementUnitName.Point, 0.01m, "USD", DefaultSpreadUnits: 30m, DefaultSlippageUnits: 10m),
        new InstrumentMetadata("XAG/USD", "metal", 2, 0.001m, MovementUnitName.Point, 0.001m, "USD", DefaultSpreadUnits: 3m, DefaultSlippageUnits: 1m),
        new InstrumentMetadata("WTI/USD", "energy", 2, 0.01m, MovementUnitName.Point, 0.01m, "USD", DefaultSpreadUnits: 4m, DefaultSlippageUnits: 1.5m),
        new InstrumentMetadata("BRENT/USD", "energy", 2, 0.01m, MovementUnitName.Point, 0.01m, "USD", DefaultSpreadUnits: 4m, DefaultSlippageUnits: 1.5m),
        // Crypto has no "pip" concept at all - movement is reported directly
        // in quote-currency terms (section 13: "Do not call Bitcoin dollar
        // movement pips"). MovementUnitSize = 1 means "the number IS the
        // quote-currency amount", displayed as points/quote-currency, never pips.
        // Spread/slippage in real dollar terms for a major-pair perpetual.
        new InstrumentMetadata("BTC/USDT", "crypto", 0, 1m, MovementUnitName.Point, 1m, "USDT", DefaultSpreadUnits: 5m, DefaultSlippageUnits: 3m),
        new InstrumentMetadata("ETH/USDT", "crypto", 0, 1m, MovementUnitName.Point, 1m, "USDT", DefaultSpreadUnits: 0.5m, DefaultSlippageUnits: 0.3m),
        // Priced around $2.55 at verification time (much lower price and
        // liquidity than BTC/ETH) - smaller quote-currency spread/slippage
        // to match, sized off Coinbase's real ~0.003 bid/ask at the time.
        new InstrumentMetadata("CAKE/USDT", "crypto", 3, 0.001m, MovementUnitName.Point, 1m, "USDT", DefaultSpreadUnits: 0.004m, DefaultSlippageUnits: 0.003m),
    };

    public static InstrumentMetadata For(string symbol) =>
        All.FirstOrDefault(m => m.Symbol == symbol)
        ?? throw new KeyNotFoundException($"No InstrumentMetadata configured for '{symbol}' - add it to InstrumentMetadataCatalog rather than guessing a unit size.");

    public static bool TryGet(string symbol, out InstrumentMetadata? metadata)
    {
        metadata = All.FirstOrDefault(m => m.Symbol == symbol);
        return metadata is not null;
    }
}
