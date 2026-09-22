namespace RealtouchSmartTrade.Api.Models;

// Section 13: central movement-unit configuration, one row per tradeable
// symbol. Real distinction the brief is explicit about: a fifth decimal
// place on non-JPY FX (or third on JPY pairs) is a pipette/fractional pip,
// not a full pip - MovementUnitSize is the size of ONE full unit in price
// terms, not "the smallest price increment the feed reports".
public enum MovementUnitName { Pip, Point, Tick }

public record InstrumentMetadata(
    string Symbol,
    string AssetClass, // "fx" | "metal" | "energy" | "crypto"
    int PriceDecimals,
    decimal TickSize,
    MovementUnitName MovementUnitName,
    decimal MovementUnitSize,
    string QuoteCurrency,
    decimal? ContractSize = null
);

public static class InstrumentMetadataCatalog
{
    // Kept in sync with SetupDefinition.cs's real instrument list. A symbol
    // missing here is a real configuration gap, not silently assumed -
    // PipCalculator.For() throws rather than guessing a unit size.
    private static readonly IReadOnlyList<InstrumentMetadata> All = new[]
    {
        new InstrumentMetadata("EUR/USD", "fx", 4, 0.00001m, MovementUnitName.Pip, 0.0001m, "USD"),
        new InstrumentMetadata("GBP/USD", "fx", 4, 0.00001m, MovementUnitName.Pip, 0.0001m, "USD"),
        // JPY-quoted pairs: 2 display decimals, pip is the SECOND decimal
        // (0.01), not the fourth - a pipette here is the third decimal.
        new InstrumentMetadata("GBP/JPY", "fx", 2, 0.001m, MovementUnitName.Pip, 0.01m, "JPY"),
        new InstrumentMetadata("XAU/USD", "metal", 1, 0.01m, MovementUnitName.Point, 0.01m, "USD"),
        new InstrumentMetadata("XAG/USD", "metal", 2, 0.001m, MovementUnitName.Point, 0.001m, "USD"),
        new InstrumentMetadata("WTI/USD", "energy", 2, 0.01m, MovementUnitName.Point, 0.01m, "USD"),
        new InstrumentMetadata("BRENT/USD", "energy", 2, 0.01m, MovementUnitName.Point, 0.01m, "USD"),
        // Crypto has no "pip" concept at all - movement is reported directly
        // in quote-currency terms (section 13: "Do not call Bitcoin dollar
        // movement pips"). MovementUnitSize = 1 means "the number IS the
        // quote-currency amount", displayed as points/quote-currency, never pips.
        new InstrumentMetadata("BTC/USDT", "crypto", 0, 1m, MovementUnitName.Point, 1m, "USDT"),
        new InstrumentMetadata("ETH/USDT", "crypto", 0, 1m, MovementUnitName.Point, 1m, "USDT"),
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
