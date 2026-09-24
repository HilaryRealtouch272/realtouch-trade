using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

// Regression: with the economic-calendar feed down (an invalid Finnhub API
// key), every model's "no hard news veto" check came back NotEvaluated, and
// NotEvaluated blocked qualification - so over 49 hours 0 of 12,553 detected
// setups qualified, 96 of them purely because of this one unavailable check
// (score above threshold, valid plan, no failed gate). A dead feed must not
// freeze all trading, but it must never be reported as "no veto" either.
public class UnavailableCalendarTests
{
    private static SetupCandidate Candidate(params RequirementStatus[] statuses) => new(
        SetupModelType.TrendContinuationPullback, SetupDirection.Short, Timeframe.H4,
        statuses.Select((s, i) => new RequirementCheck($"requirement {i}", s)).ToList());

    [Fact]
    public void AnUnavailableCalendarNoLongerBlocksQualification()
    {
        var c = Candidate(RequirementStatus.Met, RequirementStatus.Met, RequirementStatus.Unavailable);

        Assert.True(c.Qualified);
    }

    [Fact]
    public void ARealHardVetoStillBlocksQualification()
    {
        var c = Candidate(RequirementStatus.Met, RequirementStatus.NotMet);

        Assert.False(c.Qualified);
    }

    [Fact]
    public void ASetupCheckThatWasNeverAssessedStillBlocksQualification()
    {
        // Unavailable is only for an external feed being down. A requirement
        // about the setup itself that was never evaluated is a real unknown.
        var c = Candidate(RequirementStatus.Met, RequirementStatus.NotEvaluated, RequirementStatus.Unavailable);

        Assert.False(c.Qualified);
    }

    [Theory]
    [InlineData(null, RequirementStatus.NotEvaluated)]                       // not supplied at all: still a real unknown
    [InlineData(CalendarVetoState.Unavailable, RequirementStatus.Unavailable)] // feed down: does not block, never "clear"
    [InlineData(CalendarVetoState.HardVeto, RequirementStatus.NotMet)]
    [InlineData(CalendarVetoState.NoVeto, RequirementStatus.Met)]
    public void TheNewsVetoCheckMapsEachCalendarStateHonestly(CalendarVetoState? state, RequirementStatus expected)
    {
        Assert.Equal(expected, SetupModels.EvaluateNewsVeto(state).Status);
    }

    private static SignalResult Signal(string calendarState) => new(
        SignalId: "x", StrategyVersion: "v1", Symbol: "EUR/USD", AssetClass: "fx", DataProvider: "t",
        AnalysisTimeframe: "4H", EntryTimeframe: "4H", Direction: SetupDirection.Short, Condition: MarketCondition.TrendingBearish,
        SetupModel: SetupModelType.TrendContinuationPullback, Status: SignalState.Triggered, SetupQualityScore: 80, Grade: "B",
        DetectedAtUtc: DateTime.UtcNow, ExpiryUtc: DateTime.UtcNow.AddDays(1), LivePrice: 1.14m, EntryZoneMin: 1.14m,
        EntryZoneMax: 1.15m, PreferredEntry: 1.145m, Triggered: true, Stop: 1.16m, Tp1: 1.13m, Tp2: 1.12m, Tp3: 1.10m,
        RewardToRisk: 2m, RiskPercent: 0.5m, RiskAmount: 50m, PositionSize: 1m,
        KeyLevels: Array.Empty<KeyLevel>(), ConfluenceFamilies: Array.Empty<ConfluenceFamilyScore>(),
        ReasoningSummary: "t", NewsState: "Unchecked", EconomicCalendarState: calendarState, VolatilityState: "n/a",
        SessionState: "n/a", DataFreshness: "t", InvalidationConditions: "Price closes beyond 1.16. Stop reason.");

    [Fact]
    public void AnAlertSentWithoutACalendarCheckSaysSoPlainly()
    {
        var down = TelegramSignalFormatter.Format(Signal("Unavailable: Finnhub 401 invalid API key"), qualified: true);
        var clear = TelegramSignalFormatter.Format(Signal("NoVeto: no active hard news or economic event"), qualified: true);

        Assert.Contains("Economic calendar could not be checked", down);
        Assert.Contains("Check high-impact news yourself", down);
        Assert.DoesNotContain("could not be checked", clear);
        Assert.DoesNotContain("_", down); // Telegram Markdown mode
    }
}
