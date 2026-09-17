using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class EconomicCalendarVetoTests
{
    private static EconomicEvent Event(string title, string currency, string impact, DateTime scheduledUtc) =>
        new(title, currency, impact, scheduledUtc, null, null, null, "Test", DateTime.UtcNow);

    [Fact]
    public void ReturnsUnavailableWhenTheCalendarProviderItselfIsUnavailable()
    {
        var calendar = new CalendarResult(false, "Finnhub API key not configured", Array.Empty<EconomicEvent>());

        var result = EconomicCalendarVeto.Evaluate(calendar, new[] { "USD" }, DateTime.UtcNow);

        Assert.Equal(CalendarVetoState.Unavailable, result.State);
    }

    [Fact]
    public void VetoesInsideTheWideWindowForACentralBankEvent()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var events = new[] { Event("FOMC Rate Decision", "USD", "high", now.AddMinutes(40)) }; // 40 min before, within the 60-min window
        var calendar = new CalendarResult(true, null, events);

        var result = EconomicCalendarVeto.Evaluate(calendar, new[] { "USD" }, now);

        Assert.Equal(CalendarVetoState.HardVeto, result.State);
    }

    [Fact]
    public void DoesNotVetoACentralBankEventOutsideTheWideWindow()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var events = new[] { Event("FOMC Rate Decision", "USD", "high", now.AddMinutes(90)) }; // outside 60-min window
        var calendar = new CalendarResult(true, null, events);

        var result = EconomicCalendarVeto.Evaluate(calendar, new[] { "USD" }, now);

        Assert.Equal(CalendarVetoState.NoVeto, result.State);
    }

    [Fact]
    public void UsesTheNarrowerWindowForAnOrdinaryHighImpactEvent()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        // 40 minutes before a non-major high-impact event - outside its 30-min window, so no veto,
        // even though it WOULD have been vetoed if it were a major (CPI/rate/employment) event.
        var events = new[] { Event("Retail Sales", "USD", "high", now.AddMinutes(40)) };
        var calendar = new CalendarResult(true, null, events);

        var result = EconomicCalendarVeto.Evaluate(calendar, new[] { "USD" }, now);

        Assert.Equal(CalendarVetoState.NoVeto, result.State);
    }

    [Fact]
    public void IgnoresEventsForCurrenciesNotAffectingThisInstrument()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var events = new[] { Event("FOMC Rate Decision", "USD", "high", now.AddMinutes(10)) };
        var calendar = new CalendarResult(true, null, events);

        var result = EconomicCalendarVeto.Evaluate(calendar, new[] { "JPY" }, now); // instrument only cares about JPY

        Assert.Equal(CalendarVetoState.NoVeto, result.State);
    }

    [Fact]
    public void FlagsCautionForANearbyMediumImpactEventWithoutHardVetoing()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var events = new[] { Event("Housing Starts", "USD", "medium", now.AddMinutes(30)) };
        var calendar = new CalendarResult(true, null, events);

        var result = EconomicCalendarVeto.Evaluate(calendar, new[] { "USD" }, now);

        Assert.Equal(CalendarVetoState.Caution, result.State);
    }

    [Fact]
    public void NoEventsAtAllProducesNoVeto()
    {
        var calendar = new CalendarResult(true, null, Array.Empty<EconomicEvent>());

        var result = EconomicCalendarVeto.Evaluate(calendar, new[] { "USD" }, DateTime.UtcNow);

        Assert.Equal(CalendarVetoState.NoVeto, result.State);
    }
}
