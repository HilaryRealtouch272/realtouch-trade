using System.Globalization;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Free-tier, keyed, documented news API (100 requests a day on the free plan;
// no scraping). Needs a free key from marketaux.com, kept server-side
// (Marketaux:ApiKey / Marketaux:ApiKeys). Returns an honest "unavailable"
// result - never invented headlines - when the key is missing, the daily quota
// is spent or the response is not what was expected.
public class MarketauxNewsProvider(IHttpClientFactory httpClientFactory, IConfiguration config) : INewsProvider
{
    // Plain-language search terms per instrument: Marketaux's free search is
    // text based, so the wording matters more than a ticker.
    internal static readonly Dictionary<string, string> SearchTerms = new()
    {
        ["BTC/USDT"] = "bitcoin",
        ["ETH/USDT"] = "ethereum",
        ["XRP/USDT"] = "ripple XRP",
        ["SOL/USDT"] = "solana",
        ["BNB/USDT"] = "binance BNB",
        ["CAKE/USDT"] = "pancakeswap",
        ["EUR/USD"] = "euro dollar ECB Federal Reserve",
        ["GBP/USD"] = "pound sterling dollar Bank of England",
        ["GBP/JPY"] = "pound yen Bank of England Bank of Japan",
        ["USD/JPY"] = "dollar yen Bank of Japan",
        ["AUD/USD"] = "australian dollar RBA",
        ["NZD/JPY"] = "new zealand dollar yen",
        ["USD/CAD"] = "canadian dollar Bank of Canada",
        ["XAU/USD"] = "gold price",
    };

    public async Task<NewsResult> GetNewsAsync(string canonicalSymbol, CancellationToken ct = default)
    {
        var apiKeys = ApiKeys.Get(config, "Marketaux");
        if (apiKeys.Count == 0)
            return new NewsResult(false, "Marketaux API key not configured (Marketaux:ApiKey or Marketaux:ApiKeys).", Array.Empty<NewsItem>());
        if (!SearchTerms.TryGetValue(canonicalSymbol, out var terms))
            return new NewsResult(false, $"No Marketaux search terms for {canonicalSymbol}.", Array.Empty<NewsItem>());

        string? lastError = null;
        foreach (var key in apiKeys)
        {
            var result = await FetchWithKey(canonicalSymbol, terms, key, ct);
            if (result.Available) return result;
            lastError = result.UnavailableReason;
        }
        return new NewsResult(false, $"Marketaux: all {apiKeys.Count} key(s) failed. Last error: {lastError}", Array.Empty<NewsItem>());
    }

    private async Task<NewsResult> FetchWithKey(string canonicalSymbol, string terms, string apiKey, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var url = "https://api.marketaux.com/v1/news/all?language=en&limit=10&filter_entities=false" +
                      $"&published_after={Uri.EscapeDataString(DateTime.UtcNow.AddHours(-12).ToString("yyyy-MM-dd'T'HH:mm"))}" +
                      $"&search={Uri.EscapeDataString(terms)}&api_token={Uri.EscapeDataString(apiKey)}";
            var response = await client.GetAsync(url, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new NewsResult(false, $"Marketaux {(int)response.StatusCode}: {Truncate(text, 150)}", Array.Empty<NewsItem>());

            return Parse(canonicalSymbol, text);
        }
        catch (Exception ex)
        {
            return new NewsResult(false, $"Marketaux request failed: {ex.Message}", Array.Empty<NewsItem>());
        }
    }

    // Split out so the parsing is testable without a network.
    internal static NewsResult Parse(string canonicalSymbol, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new NewsResult(false, "Marketaux: unexpected response shape.", Array.Empty<NewsItem>());

        var items = new List<NewsItem>();
        foreach (var article in data.EnumerateArray())
        {
            var title = article.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = article.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            if (!article.TryGetProperty("published_at", out var p) ||
                !DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var published)) continue;

            // Sentiment only when the API supplies entity scores; never estimated here.
            double? sentiment = null;
            if (article.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
            {
                var scores = entities.EnumerateArray()
                    .Select(e => e.TryGetProperty("sentiment_score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : (double?)null)
                    .Where(s => s.HasValue).Select(s => s!.Value).ToList();
                if (scores.Count > 0) sentiment = scores.Average();
            }

            items.Add(new NewsItem(
                Headline: title!, Source: article.TryGetProperty("source", out var src) ? src.GetString() ?? "Marketaux" : "Marketaux",
                Url: url!, PublishedUtc: published, AffectedAssets: new[] { canonicalSymbol },
                // No relevance: Marketaux scores the entities named in an article, which are not
                // necessarily the asset (a headline about USDT gave BTC a "conflict"). Without a real
                // relevance the item is shown as context but can never move the news state.
                RelevanceScore: null, SentimentScore: sentiment));
        }
        return new NewsResult(true, null, items);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
