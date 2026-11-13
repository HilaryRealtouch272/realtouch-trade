using System.Collections.Concurrent;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Energy adapter (WTI/USD): neither Bybit, Twelve Data's free plan, nor
// Coinbase carry crude oil, so this hits Yahoo Finance's public, keyless
// chart endpoint directly. Yahoo has no native 4H granularity, so that's
// built by aggregating real 1H bars (same resampling approach as
// CoinbaseMarketDataProvider's own 4H/Weekly aggregation) - grouped by
// aligned 4-hour UTC boundary rather than raw index, since WTI's near-24h
// session has small daily/weekend gaps that would otherwise silently drift
// the boundary. Yahoo's own intraday range limits: 15m bars only go back
// ~60 days, 60m bars ~2 years - both comfortably over the 60-bar minimum
// the orchestrator requires, so no further fallback is needed.
public class YahooMarketDataProvider(IHttpClientFactory httpClientFactory) : IMarketDataProvider
{
    public string Name => "Yahoo";

    private static (string Interval, string Range) ParamsFor(Timeframe tf) => tf switch
    {
        Timeframe.M15 => ("15m", "60d"),
        Timeframe.H1 => ("60m", "730d"),
        Timeframe.H4 => ("60m", "730d"), // same raw request as H1 below - shared via _rawCache, not fetched twice
        Timeframe.Daily => ("1d", "5y"),
        Timeframe.Weekly => ("1wk", "10y"),
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    public async Task<IReadOnlyList<NormalizedCandle>> GetCandlesAsync(
        string canonicalSymbol, string providerSymbol, Timeframe timeframe, CancellationToken ct = default)
    {
        var (interval, range) = ParamsFor(timeframe);
        var raw = await FetchRawCached(providerSymbol, interval, range, ct);
        if (raw.Count == 0)
            throw new InvalidOperationException($"Yahoo: no candle data returned for {providerSymbol}");

        var receivedAt = DateTime.UtcNow;
        var duration = TimeframeConfig.Duration(timeframe);
        var candles = timeframe == Timeframe.H4
            ? Aggregate(raw, canonicalSymbol, providerSymbol, duration, receivedAt)
            : raw.Select((r, i) => ToNormalized(r, canonicalSymbol, providerSymbol, timeframe, duration, i == raw.Count - 1, receivedAt)).ToList();

        return CandleQualityChecker.Annotate(candles, timeframe, receivedAt);
    }

    private readonly record struct RawCandle(DateTime OpenTimeUtc, decimal Open, decimal High, decimal Low, decimal Close);

    // H1 and H4 request the exact same "60m" series from Yahoo (H4 just aggregates it 4x) -
    // without this, every scan fired that request twice. A short TTL is enough to dedupe that
    // within one scan pass (both --scan-once and the always-on loop evaluate every due
    // timeframe within milliseconds of each other) while still refreshing on the next cycle.
    private static readonly TimeSpan RawCacheTtl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (List<RawCandle> Data, DateTime FetchedAtUtc)> _rawCache = new();

    private async Task<List<RawCandle>> FetchRawCached(string tickerSymbol, string interval, string range, CancellationToken ct)
    {
        var key = $"{tickerSymbol}|{interval}|{range}";
        if (_rawCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAtUtc < RawCacheTtl)
            return cached.Data;

        var raw = await FetchRaw(tickerSymbol, interval, range, ct);
        _rawCache[key] = (raw, DateTime.UtcNow);
        return raw;
    }

    private async Task<List<RawCandle>> FetchRaw(string tickerSymbol, string interval, string range, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(tickerSymbol)}?interval={interval}&range={range}";

        // One retry after a short pause on a 429 - Yahoo's free endpoint throttles bursts of
        // requests (several timeframes fetched within seconds of each other), and a brief
        // pause commonly clears that without needing a second data source.
        HttpResponseMessage response;
        string text;
        for (var attempt = 1; ; attempt++)
        {
            response = await client.GetAsync(url, ct);
            text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= 2) break;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Yahoo {(int)response.StatusCode}: {Truncate(text, 150)}");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("chart", out var chart) || !chart.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new InvalidOperationException($"Yahoo: no result for {tickerSymbol}: {Truncate(text, 150)}");

        var root = result[0];
        var timestamps = root.GetProperty("timestamp");
        var quote = root.GetProperty("indicators").GetProperty("quote")[0];
        var opens = quote.GetProperty("open");
        var highs = quote.GetProperty("high");
        var lows = quote.GetProperty("low");
        var closes = quote.GetProperty("close");

        var list = new List<RawCandle>();
        for (var i = 0; i < timestamps.GetArrayLength(); i++)
        {
            // Yahoo returns null across the OHLC arrays for a handful of
            // non-trading/partial bars - skipped rather than kept as a
            // fabricated zero-range candle.
            if (opens[i].ValueKind == JsonValueKind.Null || highs[i].ValueKind == JsonValueKind.Null ||
                lows[i].ValueKind == JsonValueKind.Null || closes[i].ValueKind == JsonValueKind.Null)
                continue;
            var open = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime;
            list.Add(new RawCandle(open, Convert.ToDecimal(opens[i].GetDouble()), Convert.ToDecimal(highs[i].GetDouble()),
                Convert.ToDecimal(lows[i].GetDouble()), Convert.ToDecimal(closes[i].GetDouble())));
        }
        list.Sort((a, b) => a.OpenTimeUtc.CompareTo(b.OpenTimeUtc));
        return list;
    }

    private static NormalizedCandle ToNormalized(RawCandle r, string canonicalSymbol, string providerSymbol, Timeframe tf, TimeSpan duration, bool isLast, DateTime receivedAt) =>
        new(canonicalSymbol, providerSymbol, "Yahoo", tf, r.OpenTimeUtc, r.OpenTimeUtc + duration,
            r.Open, r.High, r.Low, r.Close, null, !isLast && r.OpenTimeUtc + duration <= receivedAt, receivedAt, DataQuality.Ok);

    private static DateTime AlignToFourHour(DateTime t) => new(t.Year, t.Month, t.Day, (t.Hour / 4) * 4, 0, 0, DateTimeKind.Utc);

    private static List<NormalizedCandle> Aggregate(
        List<RawCandle> raw, string canonicalSymbol, string providerSymbol, TimeSpan duration, DateTime receivedAt)
    {
        var groups = raw.GroupBy(r => AlignToFourHour(r.OpenTimeUtc)).OrderBy(g => g.Key).ToList();
        var lastBoundary = groups[^1].Key;
        var result = new List<NormalizedCandle>();
        foreach (var group in groups)
        {
            var bars = group.OrderBy(b => b.OpenTimeUtc).ToList();
            var isLastGroup = group.Key == lastBoundary;
            result.Add(new NormalizedCandle(
                canonicalSymbol, providerSymbol, "Yahoo", Timeframe.H4,
                group.Key, group.Key + duration,
                bars[0].Open, bars.Max(b => b.High), bars.Min(b => b.Low), bars[^1].Close,
                null, !isLastGroup && group.Key + duration <= receivedAt, receivedAt, DataQuality.Ok));
        }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
