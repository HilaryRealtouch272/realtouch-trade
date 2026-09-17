using System.Collections.Concurrent;

namespace RealtouchSmartTrade.Api.Services;

// Holds the most recent OrchestratorResult per (symbol, timeframe), written
// only by SignalScanBackgroundService. The /api/signals/* endpoints read
// from here instead of calling SignalOrchestrator themselves - so the
// dashboard being open/closed/refreshed never changes how often the real
// providers get called; the background service's own schedule is the only
// thing that does.
public class LatestSignalsStore
{
    private readonly ConcurrentDictionary<string, OrchestratorResult> _latest = new();

    private static string Key(string symbol, string timeframe) => $"{symbol}|{timeframe}";

    public void Set(OrchestratorResult result) => _latest[Key(result.InstrumentSymbol, result.Timeframe)] = result;

    public OrchestratorResult? Get(string symbol, string timeframe) =>
        _latest.TryGetValue(Key(symbol, timeframe), out var result) ? result : null;
}
