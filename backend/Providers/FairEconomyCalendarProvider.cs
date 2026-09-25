using System.Globalization;
using System.Text.Json;
using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// Keyless weekly economic calendar (a public JSON mirror of the ForexFactory
// calendar, served by FairEconomy). Unofficial: no published terms or uptime
// promise, current week only, forecast and previous but no actual value. Any
// failure is an honest "unavailable" result - never invented events - and
// "unavailable" never blocks a signal (the alert warns the user instead).
public class FairEconomyCalendarProvider(IHttpClientFactory httpClientFactory) : IEconomicCalendarProvider
{
    private const string Url = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";

    public async Task<CalendarResult> GetUpcomingEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RealtouchSmartTrade/1.0)");
            var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new CalendarResult(false, $"Calendar feed {(int)response.StatusCode}: {Truncate(text, 120)}", Array.Empty<EconomicEvent>());
            return Parse(text, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            return new CalendarResult(false, $"Calendar feed request failed: {ex.Message}", Array.Empty<EconomicEvent>());
        }
    }

    // Split out so the parsing is testable without a network. Holidays are not
    // market-moving events and are skipped; impact is normalised to high/medium/low.
    internal static CalendarResult Parse(string json, DateTime nowUtc)
    {
        // Rate-limit and error replies are plain text or HTML, not a JSON array.
        if (!json.TrimStart().StartsWith('['))
            return new CalendarResult(false, $"Calendar feed: unexpected reply ({Truncate(json.Trim(), 80)})", Array.Empty<EconomicEvent>());

        using var doc = JsonDocument.Parse(json);
        var events = new List<EconomicEvent>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var impact = Str(item, "impact")?.Trim().ToLowerInvariant();
            if (impact is not ("high" or "medium" or "low")) continue;
            if (!DateTimeOffset.TryParse(Str(item, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;

            events.Add(new EconomicEvent(
                Title: Str(item, "title") ?? "Unknown event",
                CountryOrCurrency: Str(item, "country") ?? "",
                Impact: impact,
                ScheduledUtc: when.UtcDateTime,
                Actual: null,
                Forecast: NullIfEmpty(Str(item, "forecast")),
                Previous: NullIfEmpty(Str(item, "previous")),
                Source: "FairEconomy",
                LastUpdateUtc: nowUtc));
        }
        return new CalendarResult(true, null, events);
    }

    private static string? Str(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
