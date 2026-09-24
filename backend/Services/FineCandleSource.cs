using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;

namespace RealtouchSmartTrade.Api.Services;

// Where the ledger gets one-minute candles from, for the instruments whose
// provider has a cheap fine feed (crypto on Coinbase). Others return nothing and
// keep using the coarser candles.
public interface IFineCandleSource
{
    Task<IReadOnlyList<NormalizedCandle>> GetOneMinuteAsync(string symbol, DateTime sinceUtc);
}

public class FineCandleSource(IEnumerable<IMarketDataProvider> providers) : IFineCandleSource
{
    public async Task<IReadOnlyList<NormalizedCandle>> GetOneMinuteAsync(string symbol, DateTime sinceUtc)
    {
        var instrument = SetupCatalog.Instruments.FirstOrDefault(i => i.Symbol == symbol);
        if (instrument is null || instrument.Source != DataSource.Coinbase) return Array.Empty<NormalizedCandle>();
        var provider = providers.FirstOrDefault(p => p.Name == "Coinbase");
        return provider is null
            ? Array.Empty<NormalizedCandle>()
            : await provider.GetFineCandlesAsync(symbol, instrument.FeedSymbol, sinceUtc);
    }
}
