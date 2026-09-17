using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Crypto adapter. Bybit's v5 public kline endpoint is free and keyless, and
// its interval codes line up with all five required timeframes.
public class BybitMarketDataProvider(IHttpClientFactory httpClientFactory) : IMarketDataProvider
{
    public string Name => "Bybit";

    private static string IntervalCode(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => "W",
        Timeframe.Daily => "D",
        Timeframe.H4 => "240",
        Timeframe.H1 => "60",
        Timeframe.M15 => "15",
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    // providerSymbol is expected in the form "SYMBOL:category", e.g. "BTCUSDT:linear" or "BTCUSDT:spot".
    public async Task<IReadOnlyList<NormalizedCandle>> GetCandlesAsync(
        string canonicalSymbol, string providerSymbol, Timeframe timeframe, CancellationToken ct = default)
    {
        var parts = providerSymbol.Split(':');
        var symbol = parts[0];
        var category = parts.Length > 1 ? parts[1] : "linear";

        var client = httpClientFactory.CreateClient();
        var interval = IntervalCode(timeframe);
        var url = $"https://api.bybit.com/v5/market/kline?category={category}&symbol={symbol}&interval={interval}&limit=120";
        var response = await client.GetAsync(url, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bybit {(int)response.StatusCode}: {Truncate(text, 150)}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var retCode = root.GetProperty("retCode").GetInt32();
        if (retCode != 0)
            throw new InvalidOperationException($"Bybit: {root.GetProperty("retMsg").GetString()} (retCode {retCode})");

        var receivedAt = DateTime.UtcNow;
        var duration = TimeframeConfig.Duration(timeframe);
        // Bybit returns newest-first: [startTime, open, high, low, close, volume, turnover].
        var rows = root.GetProperty("result").GetProperty("list").EnumerateArray().Reverse().ToList();

        var candles = new List<NormalizedCandle>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row[0].GetString()!)).UtcDateTime;
            var isComplete = i < rows.Count - 1; // most recent bar may still be forming

            candles.Add(new NormalizedCandle(
                canonicalSymbol, providerSymbol, Name, timeframe,
                openTime, openTime + duration,
                decimal.Parse(row[1].GetString()!),
                decimal.Parse(row[2].GetString()!),
                decimal.Parse(row[3].GetString()!),
                decimal.Parse(row[4].GetString()!),
                decimal.Parse(row[5].GetString()!),
                isComplete, receivedAt, DataQuality.Ok
            ));
        }

        return CandleQualityChecker.Annotate(candles, timeframe, receivedAt);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
