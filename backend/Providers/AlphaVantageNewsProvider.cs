using System.Globalization;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;

namespace RealtouchSmartTrade.Api.Providers;

// Free-tier, keyed, documented news+sentiment API (no scraping). Requires a
// free key from alphavantage.co, kept server-side (AlphaVantage:ApiKey).
// Its response shape (headline, source, url, time, per-asset relevance and
// sentiment scores) maps directly onto the NewsItem schema. Returns an
// honest "unavailable" result rather than fabricating headlines.
public class AlphaVantageNewsProvider(IHttpClientFactory httpClientFactory, IConfiguration config, IHostEnvironment env) : INewsProvider
{
    // Keys that have told us they are out of requests for the day are skipped until the next
    // UTC day (persisted, because every scheduled scan is a fresh process), and the starting key
    // rotates so load spreads across all of them instead of always burning key 1 first.
    private readonly string _exhaustedPath = Path.Combine(env.ContentRootPath, ".cache", "alphavantage-exhausted-keys.json");
    private static readonly object ExhaustedLock = new();
    private static int _roundRobin = -1;

    private Dictionary<string, DateTime> LoadExhausted()
    {
        lock (ExhaustedLock)
        {
            var loaded = DiskCache.Load<Dictionary<string, DateTime>>(_exhaustedPath) ?? new();
            var stale = loaded.Where(kv => kv.Value.Date < DateTime.UtcNow.Date).Select(kv => kv.Key).ToList();
            foreach (var k in stale) loaded.Remove(k);
            if (stale.Count > 0) DiskCache.Save(_exhaustedPath, loaded);
            return loaded;
        }
    }

    private void MarkExhausted(string key)
    {
        lock (ExhaustedLock)
        {
            var current = DiskCache.Load<Dictionary<string, DateTime>>(_exhaustedPath) ?? new();
            current[key] = DateTime.UtcNow;
            DiskCache.Save(_exhaustedPath, current);
        }
    }

    internal static bool IsQuotaMessage(string? text) =>
        text is not null && text.Contains("per day", StringComparison.OrdinalIgnoreCase);

    // Alpha Vantage ticker syntax: e.g. "CRYPTO:BTC", "FOREX:EUR", or a plain
    // equity ticker. Adjust per-symbol as real usage confirms exact coverage.
    private static readonly Dictionary<string, string> TickerMap = new()
    {
        ["BTC/USDT"] = "CRYPTO:BTC",
        ["ETH/USDT"] = "CRYPTO:ETH",
        ["BTC/USD"] = "CRYPTO:BTC",
        ["ETH/USD"] = "CRYPTO:ETH",
        ["EUR/USD"] = "FOREX:EUR",
        ["GBP/USD"] = "FOREX:GBP",
        ["GBP/JPY"] = "FOREX:GBP",
        ["XAU/USD"] = "FOREX:XAU",
        ["XAG/USD"] = "FOREX:XAG",
    };

    public async Task<NewsResult> GetNewsAsync(string canonicalSymbol, CancellationToken ct = default)
    {
        var apiKeys = ApiKeys.Get(config, "AlphaVantage");
        if (apiKeys.Count == 0)
            return new NewsResult(false, "Alpha Vantage API key not configured (AlphaVantage:ApiKey or AlphaVantage:ApiKeys). News unavailable.", Array.Empty<NewsItem>());

        if (!TickerMap.TryGetValue(canonicalSymbol, out var ticker))
            return new NewsResult(false, $"No Alpha Vantage ticker mapping for {canonicalSymbol}.", Array.Empty<NewsItem>());

        var exhausted = LoadExhausted();
        var usable = apiKeys.Where(k => !exhausted.ContainsKey(k)).ToList();
        if (usable.Count == 0)
            return new NewsResult(false, $"Alpha Vantage: all {apiKeys.Count} key(s) are out of requests for today - not retrying until the next UTC day.", Array.Empty<NewsItem>());

        var start = Interlocked.Increment(ref _roundRobin);
        string? lastError = null;
        for (var n = 0; n < usable.Count; n++)
        {
            var apiKey = usable[(start + n) % usable.Count];
            var result = await FetchWithKey(canonicalSymbol, ticker, apiKey, ct);
            if (result.Available) return result;
            lastError = result.UnavailableReason;
            if (IsQuotaMessage(lastError)) MarkExhausted(apiKey);
        }
        return new NewsResult(false, $"Alpha Vantage: all {usable.Count} usable API key(s) failed. Last error: {lastError}", Array.Empty<NewsItem>());
    }

    private async Task<NewsResult> FetchWithKey(string canonicalSymbol, string ticker, string apiKey, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var url = $"https://www.alphavantage.co/query?function=NEWS_SENTIMENT&tickers={Uri.EscapeDataString(ticker)}&apikey={apiKey}";
            var response = await client.GetAsync(url, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new NewsResult(false, $"Alpha Vantage {(int)response.StatusCode}: {Truncate(text, 150)}", Array.Empty<NewsItem>());

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("Information", out var info) || root.TryGetProperty("Note", out info))
                return new NewsResult(false, $"Alpha Vantage: {info.GetString()}", Array.Empty<NewsItem>());
            if (!root.TryGetProperty("feed", out var feed) || feed.ValueKind != JsonValueKind.Array)
                return new NewsResult(false, "Alpha Vantage: unexpected response shape.", Array.Empty<NewsItem>());

            var items = new List<NewsItem>();
            foreach (var article in feed.EnumerateArray())
            {
                var timePublishedRaw = article.GetProperty("time_published").GetString()!;
                var published = DateTime.ParseExact(timePublishedRaw, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

                double? relevance = null, sentiment = null;
                if (article.TryGetProperty("ticker_sentiment", out var sentiments))
                {
                    foreach (var ts in sentiments.EnumerateArray())
                    {
                        if (ts.GetProperty("ticker").GetString() == ticker.Split(':').Last())
                        {
                            relevance = double.Parse(ts.GetProperty("relevance_score").GetString()!, CultureInfo.InvariantCulture);
                            sentiment = double.Parse(ts.GetProperty("ticker_sentiment_score").GetString()!, CultureInfo.InvariantCulture);
                            break;
                        }
                    }
                }

                items.Add(new NewsItem(
                    Headline: article.GetProperty("title").GetString()!,
                    Source: article.GetProperty("source").GetString() ?? "Alpha Vantage",
                    Url: article.GetProperty("url").GetString()!,
                    PublishedUtc: published,
                    AffectedAssets: new[] { canonicalSymbol },
                    RelevanceScore: relevance,
                    SentimentScore: sentiment
                ));
            }
            return new NewsResult(true, null, items);
        }
        catch (Exception ex)
        {
            return new NewsResult(false, $"Alpha Vantage request failed: {ex.Message}", Array.Empty<NewsItem>());
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
