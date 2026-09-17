namespace RealtouchSmartTrade.Api.Models;

// Section 21: economic-calendar rules. Populated only from a real provider
// response - never fabricated when the provider is unavailable.
public record EconomicEvent(
    string Title,
    string CountryOrCurrency,
    string Impact,
    DateTime ScheduledUtc,
    string? Actual,
    string? Forecast,
    string? Previous,
    string Source,
    DateTime LastUpdateUtc
);

public record CalendarResult(bool Available, string? UnavailableReason, IReadOnlyList<EconomicEvent> Events);
