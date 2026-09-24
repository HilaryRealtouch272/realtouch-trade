using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Section 3: provider adapters so the strategy engine is never tied to one
// vendor. Every implementation returns the same normalized schema.
public interface IMarketDataProvider
{
    string Name { get; }

    Task<IReadOnlyList<NormalizedCandle>> GetCandlesAsync(
        string canonicalSymbol,
        string providerSymbol,
        Timeframe timeframe,
        CancellationToken ct = default);

    // One-minute candles since the given time, used only to settle open paper
    // trades (which of a stop and a target came first, and what happened in the
    // candle a trade was entered in). Providers with no cheap fine feed keep the
    // default: empty, and the coarser candles are used instead.
    Task<IReadOnlyList<NormalizedCandle>> GetFineCandlesAsync(
        string canonicalSymbol, string providerSymbol, DateTime sinceUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<NormalizedCandle>>(Array.Empty<NormalizedCandle>());
}
