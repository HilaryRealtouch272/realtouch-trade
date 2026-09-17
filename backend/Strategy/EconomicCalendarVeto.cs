using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum CalendarVetoState { NoVeto, Caution, HardVeto, Unavailable }

public record CalendarVetoResult(CalendarVetoState State, string Reason, EconomicEvent? TriggeringEvent);

// Section 21: real veto-window logic over actual fetched calendar events -
// never a "no major events" fallback when the provider itself is unavailable
// (that's Unavailable, a distinct state, checked first).
public static class EconomicCalendarVeto
{
    // Central-bank decisions, CPI and major employment releases get the wider
    // 60-before/30-after window; everything else high-impact gets 30/15.
    private static readonly string[] MajorEventKeywords =
    {
        "rate decision", "interest rate", "cpi", "inflation", "nonfarm payroll",
        "non-farm payroll", "employment", "unemployment", "fomc", "ecb", "boe", "jobs report"
    };

    public static CalendarVetoResult Evaluate(CalendarResult calendar, IReadOnlyCollection<string> affectedCurrencies, DateTime nowUtc)
    {
        if (!calendar.Available)
            return new(CalendarVetoState.Unavailable, calendar.UnavailableReason ?? "Calendar unavailable", null);

        var relevant = calendar.Events
            .Where(e => affectedCurrencies.Contains(e.CountryOrCurrency, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var evt in relevant.Where(e => e.Impact.Equals("high", StringComparison.OrdinalIgnoreCase)))
        {
            var isMajor = MajorEventKeywords.Any(k => evt.Title.Contains(k, StringComparison.OrdinalIgnoreCase));
            var before = isMajor ? TimeSpan.FromMinutes(60) : TimeSpan.FromMinutes(30);
            var after = isMajor ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(15);

            if (nowUtc >= evt.ScheduledUtc - before && nowUtc <= evt.ScheduledUtc + after)
                return new(CalendarVetoState.HardVeto,
                    $"{evt.Title} ({evt.CountryOrCurrency}, high impact) at {evt.ScheduledUtc:u} is inside its veto window ({before.TotalMinutes:0}m before / {after.TotalMinutes:0}m after)",
                    evt);
        }

        var mediumSoon = relevant.FirstOrDefault(e =>
            e.Impact.Equals("medium", StringComparison.OrdinalIgnoreCase) &&
            Math.Abs((e.ScheduledUtc - nowUtc).TotalMinutes) <= 60);
        if (mediumSoon is not null)
            return new(CalendarVetoState.Caution, $"{mediumSoon.Title} ({mediumSoon.CountryOrCurrency}, medium impact) is within an hour - caution, not a hard veto", mediumSoon);

        return new(CalendarVetoState.NoVeto, "No qualifying high-impact event within its veto window", null);
    }
}
