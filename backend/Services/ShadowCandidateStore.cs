using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

public record ShadowRecord(string Symbol, string Timeframe, ShadowCandidate Candidate);

// Shadow-mode candidates from the four additional strategies. Recorded only -
// never alerted, never entered into the paper ledger. A setup that stays valid
// scan after scan is recorded once (per model, symbol, timeframe and direction)
// until four candles have passed.
public class ShadowCandidateStore(IHostEnvironment env)
{
    private const int MaxEntries = 1000;
    private const int DedupeCandles = 4;
    private readonly string _path = Path.Combine(env.ContentRootPath, ".cache", "shadow-candidates.json");
    private readonly List<ShadowRecord> _entries = DiskCache.Load<List<ShadowRecord>>(
        Path.Combine(env.ContentRootPath, ".cache", "shadow-candidates.json")) ?? new();
    private readonly object _lock = new();

    public void Record(string symbol, string timeframe, DateTime nowUtc, TimeSpan candleDuration, IReadOnlyList<ShadowCandidate> candidates)
    {
        if (candidates.Count == 0) return;
        lock (_lock)
        {
            var window = candleDuration * DedupeCandles;
            foreach (var c in candidates)
            {
                var duplicate = _entries.Any(e => e.Symbol == symbol && e.Timeframe == timeframe &&
                    e.Candidate.Model == c.Model && e.Candidate.Direction == c.Direction && nowUtc - e.Candidate.DetectedAtUtc < window);
                if (!duplicate) _entries.Add(new ShadowRecord(symbol, timeframe, c));
            }
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            DiskCache.Save(_path, _entries);
        }
    }

    public IReadOnlyList<ShadowRecord> GetAll()
    {
        lock (_lock) return _entries.OrderByDescending(e => e.Candidate.DetectedAtUtc).ToList();
    }
}
