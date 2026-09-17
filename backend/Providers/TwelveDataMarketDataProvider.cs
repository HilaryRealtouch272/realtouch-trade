using System.Globalization;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// FX / Metals / Energy adapter. Requires a free Twelve Data API key, kept
// server-side (TwelveData:ApiKey / TwelveData:ApiKeys via dotnet user-secrets
// or env var) - never shipped to the frontend.
public class TwelveDataMarketDataProvider(IHttpClientFactory httpClientFactory, IConfiguration config) : IMarketDataProvider
{
    public string Name => "Twelve Data";

    // Symbols confirmed (via real error responses) permanently unavailable on
    // the free plan/symbol set - skipped with zero network calls rather than
    // retried every cycle. Kept in sync with Services/MarketDataService.cs.
    private static readonly HashSet<string> KnownUnavailableSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "WTI/USD", "XAG/USD", "BRENT/USD"
    };

    private static int _keyRoundRobinCounter = -1;

    private static string Interval(Timeframe tf) => tf switch
    {
        Timeframe.Weekly => "1week",
        Timeframe.Daily => "1day",
        Timeframe.H4 => "4h",
        Timeframe.H1 => "1h",
        Timeframe.M15 => "15min",
        _ => throw new ArgumentOutOfRangeException(nameof(tf))
    };

    public async Task<IReadOnlyList<NormalizedCandle>> GetCandlesAsync(
        string canonicalSymbol, string providerSymbol, Timeframe timeframe, CancellationToken ct = default)
    {
        if (KnownUnavailableSymbols.Contains(providerSymbol))
            throw new InvalidOperationException($"{providerSymbol} is known-unavailable on the free Twelve Data plan/symbol set - skipped without an API call. See DATA_SOURCES.md.");

        var apiKeys = ApiKeys.Get(config, "TwelveData");
        if (apiKeys.Count == 0)
            throw new InvalidOperationException("Twelve Data API key not configured (TwelveData:ApiKey or TwelveData:ApiKeys).");

        // Round-robin the starting key so repeated calls spread load across
        // all configured keys instead of hammering key 0 every time.
        var start = Interlocked.Increment(ref _keyRoundRobinCounter);
        var ordered = Enumerable.Range(0, apiKeys.Count).Select(i => apiKeys[(start + i) % apiKeys.Count]);

        Exception? lastError = null;
        foreach (var apiKey in ordered)
        {
            try
            {
                return await FetchWithKey(canonicalSymbol, providerSymbol, timeframe, apiKey, ct);
            }
            catch (TwelveDataNonRetryableException ex)
            {
                // A plan restriction or bad-symbol error fails identically on
                // every key - don't waste the other keys' quota retrying it.
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new InvalidOperationException($"Twelve Data: all {apiKeys.Count} API key(s) failed. Last error: {lastError?.Message}", lastError);
    }

    private async Task<IReadOnlyList<NormalizedCandle>> FetchWithKey(
        string canonicalSymbol, string providerSymbol, Timeframe timeframe, string apiKey, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        var interval = Interval(timeframe);
        var url = $"https://api.twelvedata.com/time_series?symbol={Uri.EscapeDataString(providerSymbol)}&interval={interval}&outputsize=120&apikey={apiKey}";
        var response = await client.GetAsync(url, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : "Twelve Data request failed";
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException(message); // rate limit / daily quota - genuinely key-specific, worth rotating
            throw new TwelveDataNonRetryableException(message ?? "Twelve Data request failed");
        }
        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Twelve Data request failed: no values returned");

        var receivedAt = DateTime.UtcNow;
        var duration = TimeframeConfig.Duration(timeframe);
        var rows = values.EnumerateArray().Reverse().ToList(); // API returns newest-first

        var candles = new List<NormalizedCandle>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var v = rows[i];
            var openTime = DateTime.Parse(v.GetProperty("datetime").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var isComplete = i < rows.Count - 1; // most recent bar may still be forming

            candles.Add(new NormalizedCandle(
                canonicalSymbol, providerSymbol, Name, timeframe,
                openTime, openTime + duration,
                decimal.Parse(v.GetProperty("open").GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(v.GetProperty("high").GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(v.GetProperty("low").GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(v.GetProperty("close").GetString()!, CultureInfo.InvariantCulture),
                null, // Twelve Data FX/metals/energy volume is unreliable - omit rather than invent.
                isComplete, receivedAt, DataQuality.Ok
            ));
        }

        return CandleQualityChecker.Annotate(candles, timeframe, receivedAt);
    }

    private class TwelveDataNonRetryableException(string message) : Exception(message);
}
