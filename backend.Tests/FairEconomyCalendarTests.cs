using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class FairEconomyCalendarTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private const string Sample = """
    [
      {"title":"Bank Holiday","country":"JPY","date":"2026-09-20T19:00:00-04:00","impact":"Holiday","forecast":"","previous":""},
      {"title":"Rightmove HPI m/m","country":"GBP","date":"2026-09-20T19:01:00-04:00","impact":"Low","forecast":"","previous":"-2.0%"},
      {"title":"FOMC Statement","country":"USD","date":"2026-09-24T14:00:00-04:00","impact":"High","forecast":"","previous":""},
      {"title":"Unemployment Claims","country":"USD","date":"2026-09-24T08:30:00-04:00","impact":"Medium","forecast":"215K","previous":"219K"}
    ]
    """;

    [Fact]
    public void EventsAreConvertedToUtcNormalisedAndHolidaysDropped()
    {
        var result = FairEconomyCalendarProvider.Parse(Sample, Now);

        Assert.True(result.Available);
        Assert.Equal(3, result.Events.Count);                       // the holiday is skipped
        var fomc = result.Events.Single(e => e.Title == "FOMC Statement");
        Assert.Equal(new DateTime(2026, 9, 24, 18, 0, 0, DateTimeKind.Utc), fomc.ScheduledUtc);   // 14:00 at -04:00
        Assert.Equal("high", fomc.Impact);
        Assert.Equal("USD", fomc.CountryOrCurrency);
        var claims = result.Events.Single(e => e.Title == "Unemployment Claims");
        Assert.Equal("215K", claims.Forecast);
        Assert.Null(result.Events.Single(e => e.Title == "Rightmove HPI m/m").Forecast);   // empty string becomes null
    }

    [Fact]
    public void ARateLimitOrErrorReplyIsUnavailableNotAnEmptyCalendar()
    {
        var result = FairEconomyCalendarProvider.Parse("Too many requests, please slow down", Now);

        Assert.False(result.Available);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void AHighImpactEventInsideItsWindowVetoesTheRightCurrencyOnly()
    {
        var calendar = FairEconomyCalendarProvider.Parse(Sample, Now);
        var thirtyBefore = new DateTime(2026, 9, 24, 17, 40, 0, DateTimeKind.Utc);   // FOMC is a "major" event: 60m before

        Assert.Equal(CalendarVetoState.HardVeto, EconomicCalendarVeto.Evaluate(calendar, new[] { "USD", "EUR" }, thirtyBefore).State);
        Assert.Equal(CalendarVetoState.NoVeto, EconomicCalendarVeto.Evaluate(calendar, new[] { "GBP", "JPY" }, thirtyBefore).State);
    }
}
