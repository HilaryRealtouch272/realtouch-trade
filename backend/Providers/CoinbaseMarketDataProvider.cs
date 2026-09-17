using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Crypto adapter, replacing Bybit for the real engine: Bybit's CDN geo-blocks
// GitHub Actions' hosted-runner IPs (confirmed via a real 403 CloudFront
// error on a live run), so this uses Coinbase Exchange's free, keyless
// public candle API instead - no key, no daily quota.
//
// Coinbase's candle granularities are fixed: 60/300/900/3600/21600/86400
// seconds. 15m, 1H and Daily map directly onto that. There is no native 4H
// or Weekly granularity, so those are built by aggregating 4 real 1H candles
// / 7 real daily candles into one bar using standard OHLC resampling (first
// open, last close, max high, min low, summed volume) - always derived from
// real sub-candles, never an invented number.
public class CoinbaseMarketDataProvider(IHttpClientFactory httpClientFactory) : IMarketDataProvider
{
    public string Name => "Coinbase";

    private const int TargetBars = 120;
    private const int MaxCandlesPerRequest = 300; // Coinbase's hard per-request cap
    private const int MaxPages = 4; // bounds worst-case latency/calls for weekly aggregation

    public async Task<IReadOnlyList<NormalizedCandle>> GetCandlesAsync(
        string canonicalSymbol, string providerSymbol, Timeframe timeframe, CancellationToken ct = default)
    {
        var (granularitySeconds, aggregateFactor) = GranularityFor(timeframe);
        var rawNeeded = TargetBars * aggregateFactor;
        var raw = await FetchRawCandles(providerSymbol, granularitySeconds, rawNeeded, ct);
        if (raw.Count == 0)
            throw new InvalidOperationException($"Coinbase: no candle data returned for {providerSymbol}");

        var receivedAt = DateTime.UtcNow;
        var duration = TimeframeConfig.Duration(timeframe);
        var candles = aggregateFactor == 1
            ? raw.Select((r, i) => ToNormalized(r, canonicalSymbol, providerSymbol, timeframe, duration, i == raw.Count - 1, receivedAt)).ToList()
            : Aggregate(raw, aggregateFactor, canonicalSymbol, providerSymbol, timeframe, duration, receivedAt);

        return CandleQualityChecker.Annotate(candles, timeframe, receivedAt);
    }

    private static (int GranularitySeconds, int AggregateFactor) GranularityFor(Timeframe tf) => tf switch
    {
        Timeframe.M15 => (900, 1),
        Timeframe.H1 => (3600, 1),
        Timeframe.Daily => (86400, 1),
        Timeframe.H4 => (3600, 4),
        Timeframe.Weekly => (86400, 7),
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    private readonly record struct RawCandle(DateTime OpenTimeUtc, decimal Low, decimal High, decimal Open, decimal Close, decimal Volume);

    // Coinbase caps each request at 300 candles - weekly (7x daily) needs
    // enough raw daily bars for 60+ aggregated weekly bars, which can exceed
    // that cap, so this walks backward in pages using start/end until enough
    // raw history is collected or MaxPages is hit.
    private async Task<List<RawCandle>> FetchRawCandles(string productId, int granularitySeconds, int rawNeeded, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        var all = new List<RawCandle>();
        var end = DateTime.UtcNow;

        for (int page = 0; page < MaxPages && all.Count < rawNeeded; page++)
        {
            var start = end - TimeSpan.FromSeconds((double)granularitySeconds * MaxCandlesPerRequest);
            var url = $"https://api.exchange.coinbase.com/products/{productId}/candles" +
                      $"?granularity={granularitySeconds}&start={Uri.EscapeDataString(start.ToString("o"))}&end={Uri.EscapeDataString(end.ToString("o"))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Coinbase's public API rejects requests with no/default User-Agent
            // as a bot-protection measure - confirmed via a real 400 response
            // ("User-Agent header is required") when this was missing.
            request.Headers.UserAgent.ParseAdd("RealtouchSmartTrade/1.0 (+https://github.com/HilaryRealtouch272/realtouch-trade)");
            var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Coinbase {(int)response.StatusCode}: {Truncate(text, 150)}");

            using var doc = JsonDocument.Parse(text);
            var pageRows = new List<RawCandle>();
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                // Coinbase candle shape: [ time, low, high, open, close, volume ].
                var cells = row.EnumerateArray().ToList();
                pageRows.Add(new RawCandle(
                    DateTimeOffset.FromUnixTimeSeconds(cells[0].GetInt64()).UtcDateTime,
                    cells[1].GetDecimal(), cells[2].GetDecimal(), cells[3].GetDecimal(), cells[4].GetDecimal(), cells[5].GetDecimal()));
            }
            if (pageRows.Count == 0) break; // no more history available
            all.AddRange(pageRows);
            end = pageRows.Min(r => r.OpenTimeUtc);
        }

        return all.GroupBy(r => r.OpenTimeUtc).Select(g => g.First()).OrderBy(r => r.OpenTimeUtc).ToList();
    }

    private static NormalizedCandle ToNormalized(RawCandle r, string canonicalSymbol, string providerSymbol, Timeframe tf, TimeSpan duration, bool isLast, DateTime receivedAt) =>
        new(canonicalSymbol, providerSymbol, "Coinbase", tf, r.OpenTimeUtc, r.OpenTimeUtc + duration,
            r.Open, r.High, r.Low, r.Close, r.Volume, !isLast, receivedAt, DataQuality.Ok);

    private static List<NormalizedCandle> Aggregate(
        List<RawCandle> raw, int factor, string canonicalSymbol, string providerSymbol, Timeframe tf, TimeSpan duration, DateTime receivedAt)
    {
        var result = new List<NormalizedCandle>();
        for (int i = 0; i + factor <= raw.Count; i += factor)
        {
            var group = raw.GetRange(i, factor);
            var isLastGroup = i + factor >= raw.Count;
            result.Add(new NormalizedCandle(
                canonicalSymbol, providerSymbol, "Coinbase", tf,
                group[0].OpenTimeUtc, group[0].OpenTimeUtc + duration,
                group[0].Open, group.Max(c => c.High), group.Min(c => c.Low), group[^1].Close,
                group.Sum(c => c.Volume), !isLastGroup, receivedAt, DataQuality.Ok));
        }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
