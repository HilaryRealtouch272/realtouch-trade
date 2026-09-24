using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

// The owner's explicit rule: a detected setup with a real trade plan scoring
// 76 or higher is a tradable, alerted, logged and tracked signal even when a
// mandatory gate is unconfirmed or the score is under its own model's threshold.
public class ScoreFloorTests
{
    private static StrategyEvaluation Eval(
        int score, int threshold, bool gatesPassed, bool promoted, bool detected = true,
        ReasonCode[]? failed = null, ReasonCode[]? warnings = null,
        SetupModelType model = SetupModelType.RangeBoundaryRejection) => new(
        model, "v-test", detected, gatesPassed, score, "B", threshold,
        Array.Empty<ConfluenceFamilyScore>(), failed ?? Array.Empty<ReasonCode>(), warnings ?? Array.Empty<ReasonCode>(),
        Candidate: null, Direction: SetupDirection.Short, ScoreFloorQualified: promoted);

    [Theory]
    [InlineData(false, 76, 75, true, true)]   // gates failed, above floor, plan exists
    [InlineData(false, 85, 78, true, true)]   // the CAKE-style case: high score, gate failed
    [InlineData(true, 77, 78, true, true)]    // gates passed but under a 78-model's own threshold
    [InlineData(true, 80, 78, true, false)]   // already qualifies normally: not "promoted"
    [InlineData(true, 75, 75, true, false)]   // normal path at the 75-model threshold
    [InlineData(false, 75, 75, true, false)]  // 75 is below the floor and gates failed
    [InlineData(false, 90, 78, false, false)] // no trade plan: nothing to trade or track
    public void PromotionFollowsTheFloorRuleExactly(bool gatesPassed, int score, int threshold, bool planExists, bool expected)
    {
        Assert.Equal(expected, StrategyEvaluation.IsPromotedByScoreFloor(gatesPassed, score, threshold, planExists));
    }

    [Fact]
    public void APromotedEvaluationIsQualifiedButStillReportsGatesHonestlyAsFailed()
    {
        var e = Eval(score: 85, threshold: 78, gatesPassed: false, promoted: true);

        Assert.True(e.Qualified);
        Assert.False(e.MandatoryGatesPassed);
    }

    [Fact]
    public void AHighScoreWithFailedGatesAndNoPromotionIsNotQualified()
    {
        Assert.False(Eval(score: 85, threshold: 78, gatesPassed: false, promoted: false).Qualified);
    }

    [Fact]
    public void ANormallyQualifiedEvaluationStillQualifiesWithoutPromotion()
    {
        Assert.True(Eval(score: 80, threshold: 78, gatesPassed: true, promoted: false).Qualified);
    }

    [Fact]
    public void FullyConfirmedOutranksAHigherScoringFloorSignal()
    {
        var confirmed = Eval(score: 78, threshold: 75, gatesPassed: true, promoted: false, model: SetupModelType.TrendContinuationPullback);
        var floor = Eval(score: 89, threshold: 75, gatesPassed: false, promoted: true, model: SetupModelType.BreakoutAndRetest);

        var primary = SignalOrchestrator.SelectPrimary(new[] { floor, confirmed });

        Assert.Same(confirmed, primary);
    }

    [Fact]
    public void AmongFloorSignalsTheHigherScoreIsChosen()
    {
        var lower = Eval(score: 77, threshold: 78, gatesPassed: false, promoted: true, model: SetupModelType.LiquiditySweepReversal);
        var higher = Eval(score: 84, threshold: 78, gatesPassed: false, promoted: true, model: SetupModelType.RangeBoundaryRejection);

        Assert.Same(higher, SignalOrchestrator.SelectPrimary(new[] { lower, higher }));
    }

    [Fact]
    public void NothingQualifiedMeansNoPrimary()
    {
        var a = Eval(score: 70, threshold: 75, gatesPassed: true, promoted: false);
        var b = Eval(score: 0, threshold: 75, gatesPassed: false, promoted: false, detected: false);

        Assert.Null(SignalOrchestrator.SelectPrimary(new[] { a, b }));
    }

    [Fact]
    public void TheNoteNamesTheUnconfirmedGatesAndIsSafeForTelegramMarkdown()
    {
        var e = Eval(score: 76, threshold: 78, gatesPassed: false, promoted: true,
            failed: new[] { ReasonCode.RANGE_BOUNDARY_NOT_REACHED });

        var note = SignalOrchestrator.BuildScoreFloorNote(e);

        Assert.Contains("range boundary not reached", note);
        Assert.Contains("under this model's own 78 threshold", note);
        // Telegram Markdown mode rejects the whole message on a stray underscore.
        Assert.DoesNotContain("_", note);
        Assert.DoesNotContain("*", note);
    }

    [Fact]
    public void WhenNoGateFailedButChecksWereNotEvaluatedTheNoteSaysSo()
    {
        var e = Eval(score: 80, threshold: 78, gatesPassed: false, promoted: true,
            warnings: new[] { ReasonCode.NEWS_VETO_ACTIVE });

        var note = SignalOrchestrator.BuildScoreFloorNote(e);

        Assert.Contains("checks not evaluated: news veto active", note);
        Assert.DoesNotContain("under this model's own", note);
    }

    [Fact]
    public void ThePromotedGradeReadsAsBAtTheFloorInsteadOfTracking()
    {
        // A 76 on a 78-threshold model is "Tracking" by its own bands, which
        // the alert and ledger rules reject - promotion re-grades it against
        // the floor so it is actually delivered.
        Assert.Equal("Tracking", ModelScoring.GradeFor(76, 78));
        Assert.Equal("B", ModelScoring.GradeFor(76, StrategyEvaluation.ScoreFloor));
        Assert.Equal("A", ModelScoring.GradeFor(85, StrategyEvaluation.ScoreFloor));
        Assert.Equal("A+", ModelScoring.GradeFor(90, StrategyEvaluation.ScoreFloor));
    }

    private static SignalResult Signal(string? note) => new(
        SignalId: "x", StrategyVersion: "v1", Symbol: "CAKE/USDT", AssetClass: "crypto", DataProvider: "Coinbase",
        AnalysisTimeframe: "1H", EntryTimeframe: "1H", Direction: SetupDirection.Short, Condition: MarketCondition.Ranging,
        SetupModel: SetupModelType.RangeBoundaryRejection, Status: SignalState.Triggered, SetupQualityScore: 85, Grade: "A",
        DetectedAtUtc: DateTime.UtcNow, ExpiryUtc: DateTime.UtcNow.AddDays(1), LivePrice: 2.55m, EntryZoneMin: 2.5m,
        EntryZoneMax: 2.6m, PreferredEntry: 2.55m, Triggered: true, Stop: 2.7m, Tp1: 2.4m, Tp2: 2.3m, Tp3: 2.1m,
        RewardToRisk: 3m, RiskPercent: 0.5m, RiskAmount: 50m, PositionSize: 100m,
        KeyLevels: Array.Empty<KeyLevel>(), ConfluenceFamilies: Array.Empty<ConfluenceFamilyScore>(),
        ReasoningSummary: "t", NewsState: "Unchecked", EconomicCalendarState: "Unavailable", VolatilityState: "n/a",
        SessionState: "n/a", DataFreshness: "t", InvalidationConditions: "Price closes beyond 2.7. Stop reason.",
        ScoreFloorNote: note);

    [Fact]
    public void TheTelegramAlertCarriesTheNoteOnlyForAFloorSignal()
    {
        var floor = TelegramSignalFormatter.Format(Signal("Score-floor signal (76+): gates not confirmed: range boundary not reached"), qualified: true);
        var normal = TelegramSignalFormatter.Format(Signal(null), qualified: true);

        Assert.Contains("Score-floor signal (76+)", floor);
        Assert.DoesNotContain("Score-floor", normal);
    }
}
