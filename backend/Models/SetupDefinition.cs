namespace RealtouchSmartTrade.Api.Models;

public enum DataSource { Bybit, TwelveData, Coinbase }

// One tradeable instrument, independent of timeframe. SetupCatalog expands
// each of these across all five required timeframes (Weekly/Daily/4H/1H/15m)
// so every pair has analysis available under every timeframe filter, rather
// than one hardcoded timeframe per instrument.
public record InstrumentDefinition(
    string BaseId,
    string Symbol,
    string Name,
    string Group,
    string Icon,
    int Decimals,
    DataSource Source,
    string FeedSymbol,
    string BybitCategory, // "linear" (USDT perpetual futures) or "spot" - ignored for TwelveData instruments
    string[] AffectedCurrencies // section 21: which economic-calendar currencies are relevant to this instrument
);

// Static metadata for one (instrument, timeframe) combination. Quantitative
// fields (score, direction, entry/stop/target, condition) are NOT here -
// they're computed live by SignalEngine from real candles, never hardcoded.
public record SetupDefinition(
    string Id,
    string Symbol,
    string Name,
    string Group,
    string Icon,
    string Timeframe,
    int Decimals,
    DataSource Source,
    string FeedSymbol,
    string BybitCategory,
    string BybitInterval,
    string TwelveDataInterval
);

public static class SetupCatalog
{
    public static readonly IReadOnlyList<InstrumentDefinition> Instruments = new[]
    {
        // Bybit's CDN geo-blocks GitHub Actions' hosted-runner IPs (confirmed
        // via a real 403 CloudFront error there), so the real engine's
        // crypto instruments now run on Coinbase Exchange's free, keyless
        // candle API instead (still Bybit for the old v1 /api/setups*
        // endpoints below, which stay untouched - see SetupDefinition.All).
        // Coinbase has no perpetual-futures product, so "Crypto Futures"
        // here honestly shares the same real spot feed as "Crypto Spot" -
        // not fabricated, just the best real substitute, called out
        // explicitly rather than silently mislabelled.
        new InstrumentDefinition("btc-f", "BTC/USDT", "Bitcoin Perpetual", "Crypto Futures", "₿", 0, DataSource.Coinbase, "BTC-USD", "", new[] { "USD" }),
        new InstrumentDefinition("eth-f", "ETH/USDT", "Ether Perpetual", "Crypto Futures", "Ξ", 0, DataSource.Coinbase, "ETH-USD", "", new[] { "USD" }),
        new InstrumentDefinition("btc-spot", "BTC/USD", "Bitcoin Spot", "Crypto Spot", "₿", 0, DataSource.Coinbase, "BTC-USD", "", new[] { "USD" }),
        new InstrumentDefinition("eth-spot", "ETH/USD", "Ether Spot", "Crypto Spot", "Ξ", 0, DataSource.Coinbase, "ETH-USD", "", new[] { "USD" }),
        new InstrumentDefinition("eurusd", "EUR/USD", "Euro / US Dollar", "FX", "€", 4, DataSource.TwelveData, "EUR/USD", "", new[] { "EUR", "USD" }),
        new InstrumentDefinition("gbpusd", "GBP/USD", "British Pound / US Dollar", "FX", "£", 4, DataSource.TwelveData, "GBP/USD", "", new[] { "GBP", "USD" }),
        new InstrumentDefinition("gbpjpy", "GBP/JPY", "British Pound / Yen", "FX", "¥", 2, DataSource.TwelveData, "GBP/JPY", "", new[] { "GBP", "JPY" }),
        new InstrumentDefinition("gold", "XAU/USD", "Gold Spot", "Metals", "Au", 1, DataSource.TwelveData, "XAU/USD", "", new[] { "USD" }),
        // Commented out: confirmed unavailable on the free Twelve Data plan/symbol
        // set (see MarketDataService.KnownUnavailableSymbols and DATA_SOURCES.md).
        // Re-enable once upgraded, or once a correct Brent symbol is confirmed.
        // new InstrumentDefinition("silver", "XAG/USD", "Silver Spot", "Metals", "Ag", 2, DataSource.TwelveData, "XAG/USD", "", new[] { "USD" }),
        // new InstrumentDefinition("wti", "WTI/USD", "Crude Oil WTI Spot", "Energy", "WTI", 2, DataSource.TwelveData, "WTI/USD", "", new[] { "USD" }),
        // new InstrumentDefinition("brent", "BRENT/USD", "Brent Crude Spot", "Energy", "BR", 2, DataSource.TwelveData, "BRENT/USD", "", new[] { "USD" }),
    };

    private static string TimeframeSuffix(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => "w",
        Timeframe.Daily => "d",
        Timeframe.H4 => "4h",
        Timeframe.H1 => "1h",
        Timeframe.M15 => "15m",
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    public static readonly IReadOnlyList<SetupDefinition> All = Instruments
        .SelectMany(instrument => TimeframeIntervals.All.Select(tf => new SetupDefinition(
            $"{instrument.BaseId}-{TimeframeSuffix(tf)}",
            instrument.Symbol, instrument.Name, instrument.Group, instrument.Icon,
            TimeframeIntervals.Label(tf), instrument.Decimals, instrument.Source, instrument.FeedSymbol,
            instrument.BybitCategory,
            instrument.Source == DataSource.Bybit ? TimeframeIntervals.BybitCode(tf) : "",
            instrument.Source == DataSource.TwelveData ? TimeframeIntervals.TwelveDataCode(tf) : ""
        )))
        .ToList();
}
