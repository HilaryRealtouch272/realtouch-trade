using System.Globalization;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Free-tier, keyed, documented economic-calendar API (no scraping). Requires
// a free key from finnhub.io, kept server-side (Finnhub:ApiKey). Returns an
// honest "unavailable" result rather than fabricating events when no key is
// configured or the request fails - never silently falls back to fake data.
public class FinnhubEconomicCalendarProvider(IHttpClientFactory httpClientFactory, IConfiguration config) : IEconomicCalendarProvider
{
    public async Task<CalendarResult> GetUpcomingEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var apiKeys = ApiKeys.Get(config, "Finnhub");
        if (apiKeys.Count == 0)
            return new CalendarResult(false, "Finnhub API key not configured (Finnhub:ApiKey or Finnhub:ApiKeys). Calendar unavailable.", Array.Empty<EconomicEvent>());

        string? lastError = null;
        foreach (var apiKey in apiKeys)
        {
            var result = await FetchWithKey(fromUtc, toUtc, apiKey, ct);
            if (result.Available) return result;
            lastError = result.UnavailableReason;
        }
        return new CalendarResult(false, $"Finnhub: all {apiKeys.Count} API key(s) failed. Last error: {lastError}", Array.Empty<EconomicEvent>());
    }

    private async Task<CalendarResult> FetchWithKey(DateTime fromUtc, DateTime toUtc, string apiKey, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            var from = fromUtc.ToString("yyyy-MM-dd");
            var to = toUtc.ToString("yyyy-MM-dd");
            var url = $"https://finnhub.io/api/v1/calendar/economic?from={from}&to={to}&token={apiKey}";
            var response = await client.GetAsync(url, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new CalendarResult(false, $"Finnhub {(int)response.StatusCode}: {Truncate(text, 150)}", Array.Empty<EconomicEvent>());

            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("economicCalendar", out var items) || items.ValueKind != JsonValueKind.Array)
                return new CalendarResult(false, "Finnhub: unexpected response shape.", Array.Empty<EconomicEvent>());

            var now = DateTime.UtcNow;
            var events = new List<EconomicEvent>();
            foreach (var item in items.EnumerateArray())
            {
                if (!DateTime.TryParse(GetString(item, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var scheduled))
                    continue;

                events.Add(new EconomicEvent(
                    Title: GetString(item, "event") ?? "Unknown event",
                    CountryOrCurrency: GetString(item, "country") ?? "",
                    Impact: GetString(item, "impact") ?? "unknown",
                    ScheduledUtc: scheduled,
                    Actual: GetString(item, "actual"),
                    Forecast: GetString(item, "estimate"),
                    Previous: GetString(item, "prev"),
                    Source: "Finnhub",
                    LastUpdateUtc: now
                ));
            }
            return new CalendarResult(true, null, events);
        }
        catch (Exception ex)
        {
            return new CalendarResult(false, $"Finnhub request failed: {ex.Message}", Array.Empty<EconomicEvent>());
        }
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
