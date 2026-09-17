using System.Text.Json;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;

namespace RealtouchSmartTrade.Api.Services;

public class MarketDataService(IHttpClientFactory httpClientFactory, IConfiguration config)
{
    // Twelve Data's free tier caps requests at roughly 8/minute PER KEY.
    // Spreading requests round-robin across all configured keys (rather than
    // always starting from key 0) lets N keys sustain ~8*N req/min combined
    // instead of bottlenecking on one key while the others sit idle.
    private static int _keyRoundRobinCounter = -1;

    // Bybit v5 public market-data endpoint - free, keyless, no daily quota.
    public async Task<IReadOnlyList<Candle>> FetchBybitCandles(string symbol, string category, string interval)
    {
        var client = httpClientFactory.CreateClient();
        var url = $"https://api.bybit.com/v5/market/kline?category={category}&symbol={symbol}&interval={interval}&limit=120";
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bybit {(int)response.StatusCode}: {Truncate(text, 150)}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var retCode = root.GetProperty("retCode").GetInt32();
        if (retCode != 0)
            throw new InvalidOperationException($"Bybit: {root.GetProperty("retMsg").GetString()} (retCode {retCode})");

        var list = root.GetProperty("result").GetProperty("list");
        // Bybit returns newest-first: [startTime, open, high, low, close, volume, turnover].
        var rows = list.EnumerateArray().Reverse().ToList();

        var candles = new List<Candle>();
        foreach (var row in rows)
        {
            candles.Add(new Candle(
                decimal.Parse(row[1].GetString()!),
                decimal.Parse(row[2].GetString()!),
                decimal.Parse(row[3].GetString()!),
                decimal.Parse(row[4].GetString()!)
            ));
        }
        return candles;
    }

    // Symbols confirmed (empirically, from real error responses) to be
    // permanently unavailable on the free Twelve Data plan or under the
    // configured symbol string. Skipped entirely - no network call, no
    // wasted quota - rather than retried every poll cycle forever.
    private static readonly HashSet<string> KnownUnavailableSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "WTI/USD",   // "This symbol is available starting with the Grow or Venture plan."
        "XAG/USD",   // same - Grow/Venture plan required
        "BRENT/USD", // "symbol or figi parameter is missing or invalid" - wrong symbol string, not a plan issue
    };

    public async Task<IReadOnlyList<Candle>> FetchTwelveDataCandles(string symbol, string interval, int outputSize = 120)
    {
        if (KnownUnavailableSymbols.Contains(symbol))
            throw new InvalidOperationException($"{symbol} is known-unavailable on the free Twelve Data plan/symbol set - skipped without an API call. See DATA_SOURCES.md.");

        var apiKeys = ApiKeys.Get(config, "TwelveData");
        if (apiKeys.Count == 0)
            throw new InvalidOperationException("Twelve Data API key not configured on the server (TwelveData:ApiKey or TwelveData:ApiKeys).");

        // Start this request on a different key than the last one (round-robin),
        // then fail over through the rest ONLY if the error is key-specific
        // (rate limit / quota) - a plan-restriction or bad-symbol error fails
        // identically on every key, so retrying them all just burns 4x the
        // quota for a guaranteed failure.
        var start = Interlocked.Increment(ref _keyRoundRobinCounter);
        var ordered = Enumerable.Range(0, apiKeys.Count).Select(i => apiKeys[(start + i) % apiKeys.Count]);

        Exception? lastError = null;
        foreach (var apiKey in ordered)
        {
            try
            {
                return await FetchTwelveDataWithKey(symbol, interval, outputSize, apiKey);
            }
            catch (TwelveDataNonRetryableException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new InvalidOperationException($"Twelve Data: all {apiKeys.Count} API key(s) failed. Last error: {lastError?.Message}", lastError);
    }

    private async Task<IReadOnlyList<Candle>> FetchTwelveDataWithKey(string symbol, string interval, int outputSize, string apiKey)
    {
        var client = httpClientFactory.CreateClient();
        var url = $"https://api.twelvedata.com/time_series?symbol={Uri.EscapeDataString(symbol)}&interval={interval}&outputsize={outputSize}&apikey={apiKey}";
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : "Twelve Data request failed";
            // HTTP 429 = rate limit or daily quota - genuinely key-specific, worth
            // trying the next key. Anything else (404 = bad symbol, plan
            // restriction, etc.) fails identically regardless of which key is
            // used, so don't waste the other keys' quota retrying it.
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException(message);
            throw new TwelveDataNonRetryableException(message ?? "Twelve Data request failed");
        }
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Twelve Data request failed: no values returned");

        var candles = new List<Candle>();
        foreach (var v in values.EnumerateArray())
        {
            candles.Add(new Candle(
                decimal.Parse(v.GetProperty("open").GetString()!),
                decimal.Parse(v.GetProperty("high").GetString()!),
                decimal.Parse(v.GetProperty("low").GetString()!),
                decimal.Parse(v.GetProperty("close").GetString()!)
            ));
        }
        candles.Reverse();
        return candles;
    }

    private class TwelveDataNonRetryableException(string message) : Exception(message);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
