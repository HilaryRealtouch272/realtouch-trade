namespace RealtouchSmartTrade.Api.Models;

// What the API returns for one instrument: static metadata + either a live
// computed signal, or an error explaining why live data isn't available.
public record SetupSnapshot(
    string Id,
    string Symbol,
    string Name,
    string Group,
    string Icon,
    string Timeframe,
    int Decimals,
    bool Live,
    string? LiveSource,
    string? Error,
    decimal? Price,
    decimal? Entry,
    decimal? Stop,
    decimal? Target,
    decimal? Rr,
    string? Direction,
    string? Condition,
    int? Score,
    string[]? Confluences,
    string? Reasoning
);
