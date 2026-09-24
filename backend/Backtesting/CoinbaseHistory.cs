using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Backtesting;

// Deep history from Coinbase's public candle endpoint: walks forward in 300-bar
// windows (the API's hard cap), paced to respect its rate limit.
public static class CoinbaseHistory
{
    private const int MaxPerRequest = 300;

    public static async Task<List<NormalizedCandle>> FetchAsync(
        HttpClient client, string canonicalSymbol, string productId, Timeframe tf, int granularitySeconds,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var span = TimeSpan.FromSeconds(granularitySeconds);
        var byOpen = new Dictionary<DateTime, NormalizedCandle>();

        for (var windowStart = fromUtc; windowStart < toUtc; windowStart += span * MaxPerRequest)
        {
            var windowEnd = windowStart + span * MaxPerRequest;
            if (windowEnd > toUtc) windowEnd = toUtc;
            var url = $"https://api.exchange.coinbase.com/products/{productId}/candles?granularity={granularitySeconds}" +
                      $"&start={Uri.EscapeDataString(windowStart.ToString("o"))}&end={Uri.EscapeDataString(windowEnd.ToString("o"))}";

            string text;
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("RealtouchSmartTrade/1.0 (backtest)");
                var response = await client.SendAsync(request, ct);
                text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) break;
                if (attempt >= 4) throw new InvalidOperationException($"Coinbase {(int)response.StatusCode}: {text[..Math.Min(150, text.Length)]}");
                await Task.Delay(1500 * (attempt + 1), ct); // rate limited: back off and retry
            }

            using var doc = JsonDocument.Parse(text);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var cells = row.EnumerateArray().ToList();
                var open = DateTimeOffset.FromUnixTimeSeconds(cells[0].GetInt64()).UtcDateTime;
                byOpen[open] = new NormalizedCandle(canonicalSymbol, productId, "Coinbase", tf, open, open + span,
                    cells[3].GetDecimal(), cells[2].GetDecimal(), cells[1].GetDecimal(), cells[4].GetDecimal(), cells[5].GetDecimal(),
                    true, DateTime.UtcNow, DataQuality.Ok);
            }
            await Task.Delay(400, ct);
        }

        return byOpen.Values.Where(c => c.OpenTimeUtc >= fromUtc && c.CloseTimeUtc <= toUtc).OrderBy(c => c.OpenTimeUtc).ToList();
    }
}
