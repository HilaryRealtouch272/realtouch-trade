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
}
