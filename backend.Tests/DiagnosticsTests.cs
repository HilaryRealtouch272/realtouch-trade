using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class DiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-diag-" + Guid.NewGuid().ToString("N"));

    private class Env : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private StrategyDiagnosticsStore NewStore() => new(new Env { ContentRootPath = _root });

    private static readonly ConfluenceFamilyScore[] Families =
    {
        new("Location", 15, 15, "ok"), new("Zone", 0, 15, "none"), new("Sweep", 10, 10, "ok")
    };

    private static StrategyEvaluation Eval(SetupModelType model, bool detected, bool gates, int score, params ReasonCode[] failed) =>
        new(model, "v", detected, gates, score, "B", 75, Families, failed, Array.Empty<ReasonCode>(), null, RewardToRisk: 2.4m);

    [Fact]
    public void EveryModelGetsAFinalDispositionAndTheEvidenceItHadAndLacked()
    {
        var store = NewStore();
        var ctx = new DiagnosticsContext("Unavailable", "London", 95, 3.0, SetupModelType.TrendContinuationPullback, false);

        store.Record("EUR/USD", "15m", new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc), MarketCondition.TrendingBullish, new[]
        {
            Eval(SetupModelType.TrendContinuationPullback, true, true, 80),
            Eval(SetupModelType.BreakoutAndRetest, true, false, 60, ReasonCode.REWARD_RISK_TOO_LOW),
            Eval(SetupModelType.RangeBoundaryRejection, false, false, 0)
        }, ctx);

        var rows = store.GetAll().ToDictionary(r => r.StrategyId);
        Assert.Equal("Qualified", rows[SetupModelType.TrendContinuationPullback].FinalDisposition);
        Assert.Equal("Rejected", rows[SetupModelType.BreakoutAndRetest].FinalDisposition);
        Assert.Equal("NoSetup", rows[SetupModelType.RangeBoundaryRejection].FinalDisposition);

        var trend = rows[SetupModelType.TrendContinuationPullback];
        Assert.Contains("Location +15/15", trend.AwardedEvidence!);
        Assert.Contains("Zone (0/15)", trend.MissingEvidence!);
        Assert.Equal(2.4m, trend.RewardToRisk);
        Assert.Equal("London", trend.Session);
        Assert.Equal(95, trend.MinutesToNextHighImpact);
        Assert.Null(rows[SetupModelType.RangeBoundaryRejection].AwardedEvidence);
    }

    [Fact]
    public void ASetupQualifiedButNotChosenOrVetoedIsSaidSo()
    {
        var qualified = Eval(SetupModelType.BreakoutAndRetest, true, true, 80);

        Assert.Equal("QualifiedNotSelected", StrategyDiagnosticsStore.Disposition(qualified,
            new DiagnosticsContext("NoVeto", "London", null, null, SetupModelType.TrendContinuationPullback, false)));
        Assert.Equal("VetoedByCalendar", StrategyDiagnosticsStore.Disposition(qualified,
            new DiagnosticsContext("HardVeto", "London", null, null, SetupModelType.BreakoutAndRetest, true)));
    }

    [Fact]
    public void TheDailyRollupAccumulatesAcrossScansAndSurvivesARestart()
    {
        var day = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
        var first = NewStore();
        first.Record("EUR/USD", "15m", day, MarketCondition.Ranging, new[] { Eval(SetupModelType.RangeBoundaryRejection, true, false, 70, ReasonCode.HTF_CONFLICT) });
        first.Record("GBP/USD", "15m", day.AddMinutes(15), MarketCondition.Ranging, new[] { Eval(SetupModelType.RangeBoundaryRejection, true, false, 72, ReasonCode.HTF_CONFLICT) });

        var restarted = NewStore();
        var tally = restarted.GetDaily()["2026-09-24"]["RangeBoundaryRejection"];

        Assert.Equal(2, tally.Evaluations);
        Assert.Equal(2, tally.Detected);
        Assert.Equal(0, tally.Qualified);
        Assert.Equal(2, tally.Gates["HTF_CONFLICT"]);
    }

    [Fact]
    public void MinutesToTheNextHighImpactEventIgnoresOtherCurrenciesLowImpactAndThePast()
    {
        var now = new DateTime(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
        EconomicEvent E(string cur, string impact, int minutes) =>
            new("Event", cur, impact, now.AddMinutes(minutes), null, null, null, "test", now);
        var calendar = new CalendarResult(true, null, new[]
        {
            E("USD", "high", -30), E("JPY", "high", 20), E("USD", "low", 10), E("USD", "high", 95), E("USD", "high", 300)
        });

        Assert.Equal(95, EconomicCalendarVeto.MinutesToNextHighImpact(calendar, new[] { "USD", "EUR" }, now));
        Assert.Null(EconomicCalendarVeto.MinutesToNextHighImpact(new CalendarResult(false, "down", Array.Empty<EconomicEvent>()), new[] { "USD" }, now));
    }

    [Fact]
    public void SessionsFollowDaylightSavingNotAFixedUtcOffset()
    {
        // 07:30 UTC is 08:30 in London in September (BST) but 07:30 in January (GMT).
        Assert.Contains("London", TradingSessions.Describe(new DateTime(2026, 9, 24, 7, 30, 0, DateTimeKind.Utc)));
        Assert.DoesNotContain("London", TradingSessions.Describe(new DateTime(2026, 1, 21, 7, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("OffHours", TradingSessions.Describe(new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc))); // Saturday
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
