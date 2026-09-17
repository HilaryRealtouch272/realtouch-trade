using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

public interface IEconomicCalendarProvider
{
    Task<CalendarResult> GetUpcomingEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
