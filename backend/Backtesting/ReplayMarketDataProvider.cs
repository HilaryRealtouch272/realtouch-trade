using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;

namespace RealtouchSmartTrade.Api.Backtesting;

// Serves historical candles to the real signal engine as if the world stopped at
// AsOf: only candles that had CLOSED by then are ever returned, so nothing from
// the future can leak into a decision (no look-ahead, no repainting). It reports
// itself as "Coinbase" because the engine picks its provider by instrument source.
public class ReplayMarketDataProvider : IMarketDataProvider
{
    private const int BarsReturned = 120; // the live provider's TargetBars

    private readonly Dictionary<(string Symbol, Timeframe Tf), List<NormalizedCandle>> _series = new();

    public string Name => "Coinbase";
    public DateTime AsOf { get; set; }

    public void AddSeries(string symbol, Timeframe tf, IEnumerable<NormalizedCandle> candles) =>
        _series[(symbol, tf)] = candles.OrderBy(c => c.OpenTimeUtc).ToList();

    public IReadOnlyList<NormalizedCandle> Series(string symbol, Timeframe tf) => _series[(symbol, tf)];

    // The completed candles visible at `asOf`, oldest first (binary search: series are sorted).
    public static IReadOnlyList<NormalizedCandle> VisibleAt(IReadOnlyList<NormalizedCandle> sorted, DateTime asOf, int take = int.MaxValue)
    {
        int lo = 0, hi = sorted.Count; // first index whose CloseTimeUtc > asOf
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid].CloseTimeUtc <= asOf) lo = mid + 1; else hi = mid;
        }
        var start = Math.Max(0, lo - take);
        var result = new List<NormalizedCandle>(lo - start);
        for (var i = start; i < lo; i++) result.Add(sorted[i]);
        return result;
    }

    public Task<IReadOnlyList<NormalizedCandle>> GetCandlesAsync(
        string canonicalSymbol, string providerSymbol, Timeframe timeframe, CancellationToken ct = default)
    {
        if (!_series.TryGetValue((canonicalSymbol, timeframe), out var series))
            throw new InvalidOperationException($"No replay history for {canonicalSymbol} {timeframe}");

        var visible = VisibleAt(series, AsOf, BarsReturned)
            .Select(c => c with { IsComplete = true, ReceivedAtUtc = AsOf }).ToList();
        return Task.FromResult<IReadOnlyList<NormalizedCandle>>(CandleQualityChecker.Annotate(visible, timeframe, AsOf));
    }

    // Aggregates finer candles into a coarser one on UTC-aligned boundaries, keeping
    // only buckets that are fully populated - a partial bucket is never invented.
    public static List<NormalizedCandle> Aggregate(IReadOnlyList<NormalizedCandle> fine, TimeSpan fineSpan, TimeSpan bucket, Timeframe tf)
    {
        var expected = (int)(bucket.Ticks / fineSpan.Ticks);
        var result = new List<NormalizedCandle>();
        foreach (var group in fine.OrderBy(c => c.OpenTimeUtc).GroupBy(c => new DateTime(c.OpenTimeUtc.Ticks - c.OpenTimeUtc.Ticks % bucket.Ticks, DateTimeKind.Utc)))
        {
            var g = group.ToList();
            if (g.Count != expected) continue;
            var first = g[0];
            result.Add(new NormalizedCandle(first.CanonicalSymbol, first.ProviderSymbol, first.Provider, tf,
                group.Key, group.Key + bucket,
                first.Open, g.Max(c => c.High), g.Min(c => c.Low), g[^1].Close,
                g.Sum(c => c.Volume ?? 0m), true, g[^1].ReceivedAtUtc, DataQuality.Ok));
        }
        return result;
    }
}
