using System.Globalization;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// GDELT: free, keyless, refreshed about every 15 minutes. It is a supplementary
// geopolitical / macro risk source only - headlines carry NO sentiment or
// relevance score here, so they can never push a signal's news state either way;
// they only add context to the alert. Any failure (including GDELT's habit of
// answering rate limits with plain text) is an honest "unavailable".
public class GdeltNewsProvider(IHttpClientFactory httpClientFactory) : INewsProvider
{
    // Boolean-style GDELT queries per instrument.
    internal static readonly Dictionary<string, string> Queries = new()
    {
        ["BTC/USDT"] = "bitcoin",
        ["ETH/USDT"] = "ethereum",
        ["XRP/USDT"] = "ripple",
        ["SOL/USDT"] = "solana",
        ["BNB/USDT"] = "binance",
        ["CAKE/USDT"] = "pancakeswap",
        ["EUR/USD"] = "(\"European Central Bank\" OR \"euro zone\" OR \"Federal Reserve\")",
        ["GBP/USD"] = "(\"Bank of England\" OR \"British pound\" OR \"Federal Reserve\")",
        ["GBP/JPY"] = "(\"Bank of England\" OR \"Bank of Japan\" OR \"British pound\")",
        ["USD/JPY"] = "(\"Bank of Japan\" OR \"Federal Reserve\" OR yen)",
        ["AUD/USD"] = "(\"Reserve Bank of Australia\" OR \"Australian dollar\")",
        ["NZD/JPY"] = "(\"Reserve Bank of New Zealand\" OR \"Bank of Japan\")",
        ["USD/CAD"] = "(\"Bank of Canada\" OR \"Canadian dollar\" OR \"crude oil\")",
        ["XAU/USD"] = "(\"gold price\" OR \"safe haven\")",
    };

    public async Task<NewsResult> GetNewsAsync(string canonicalSymbol, CancellationToken ct = default)
    {
        if (!Queries.TryGetValue(canonicalSymbol, out var query))
            return new NewsResult(false, $"No GDELT query for {canonicalSymbol}.", Array.Empty<NewsItem>());
        try
        {
            var client = httpClientFactory.CreateClient();
            var url = "https://api.gdeltproject.org/api/v2/doc/doc?mode=artlist&format=json&maxrecords=8&sort=datedesc&timespan=6h" +
                      $"&query={Uri.EscapeDataString(query + " sourcelang:english")}";
            var response = await client.GetAsync(url, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new NewsResult(false, $"GDELT {(int)response.StatusCode}", Array.Empty<NewsItem>());
            return Parse(canonicalSymbol, text);
        }
        catch (Exception ex)
        {
            return new NewsResult(false, $"GDELT request failed: {ex.Message}", Array.Empty<NewsItem>());
        }
    }

    internal static NewsResult Parse(string canonicalSymbol, string body)
    {
        // Rate-limit and error replies are plain text, not JSON.
        if (!body.TrimStart().StartsWith('{'))
            return new NewsResult(false, $"GDELT: {(body.Length > 80 ? body[..80] : body).Trim()}", Array.Empty<NewsItem>());

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("articles", out var articles) || articles.ValueKind != JsonValueKind.Array)
            return new NewsResult(true, null, Array.Empty<NewsItem>()); // no matching articles is a real, empty answer

        var items = new List<NewsItem>();
        foreach (var a in articles.EnumerateArray())
        {
            var title = a.TryGetProperty("title", out var t) ? t.GetString() : null;
            var url = a.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            if (!a.TryGetProperty("seendate", out var s) ||
                !DateTime.TryParseExact(s.GetString(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var seen)) continue;

            items.Add(new NewsItem(title!, a.TryGetProperty("domain", out var d) ? d.GetString() ?? "GDELT" : "GDELT",
                url!, seen, new[] { canonicalSymbol }, RelevanceScore: null, SentimentScore: null));
        }
        return new NewsResult(true, null, items);
    }
}
