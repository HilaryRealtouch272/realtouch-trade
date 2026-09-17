namespace RealtouchSmartTrade.Api.Models;

public enum DataQuality { Ok, Stale, Gap, Duplicate, OutOfOrder }

// Section 3: every provider is converted to this one internal schema. All
// timestamps are UTC. IsComplete distinguishes a closed candle (safe to use
// for signal confirmation) from the currently-forming one (chart-only).
public record NormalizedCandle(
    string CanonicalSymbol,
    string ProviderSymbol,
    string Provider,
    Timeframe Timeframe,
    DateTime OpenTimeUtc,
    DateTime CloseTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal? Volume,
    bool IsComplete,
    DateTime ReceivedAtUtc,
    DataQuality Quality
);
