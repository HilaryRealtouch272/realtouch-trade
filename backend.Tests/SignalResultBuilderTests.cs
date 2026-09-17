using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class SignalResultBuilderTests
{
    [Fact]
    public void AssemblesACoherentResultFromEndToEndUptrendFixture()
    {
        var bars = CandleFixtures.UptrendBars(120);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);
        var condition = MarketConditionClassifier.Classify(candles, Timeframe.Daily, structure);
        var obs = OrderBlockDetector.Detect(candles, Timeframe.Daily, structure);
        var fvgs = FvgDetector.Detect(candles, Timeframe.Daily);
        var willis = WillisZoneDetector.Detect(candles, Timeframe.Daily, structure, obs, fvgs);
        var sweeps = LiquiditySweepDetector.Detect(candles, Timeframe.Daily, structure);
        var levels = KeyLevelCatalog.Build(candles, Timeframe.Daily);

        var candidate = SetupModels.EvaluateTrendContinuationPullback(candles, Timeframe.Daily, condition, structure, obs, fvgs, willis, sweeps);
        Assert.NotNull(candidate);

        var tradePlan = EntryStopTargetCalculator.Compute(candles, Timeframe.Daily, candidate!.Direction, structure, obs, fvgs, willis, sweeps, levels);
        Assert.NotNull(tradePlan);

        var scoringInput = new ScoringInput(
            condition, candidate.Direction,
            LocationQualified: candidate.Requirements.Any(r => r.Description.Contains("discount") && r.Status == RequirementStatus.Met),
            LiquidityEventPresent: sweeps.Count > 0,
            EntryStructureEvent: structure.Events.LastOrDefault()?.Type,
            ZoneSource: tradePlan!.Entry.ZoneSource,
            DisplacementAtEntry: false,
            RewardToRisk: tradePlan.RewardToRisk
        );
        var score = ConfluenceScorer.Score(scoringInput);
        var positionSize = RiskSizing.Compute(10000m, 0m, score.Grade == "No setup" ? "B" : score.Grade, tradePlan.Entry.PreferredEntry, tradePlan.Stop.Price, isFx: false);

        var result = SignalResultBuilder.Build(
            "v1-strategy-engine", "BTC/USDT", "Crypto Futures", "Fixture",
            Timeframe.Daily, candidate, condition, tradePlan, score, positionSize,
            SignalLifecycle.InitialState(score.TotalScore), levels, candles[^1].Close, DateTime.UtcNow);

        Assert.NotEmpty(result.SignalId);
        Assert.Equal("BTC/USDT", result.Symbol);
        Assert.Equal(tradePlan.Stop.Price, result.Stop);
        Assert.Equal(score.TotalScore, result.SetupQualityScore);
        // This test calls SignalResultBuilder directly without calendar/news state
        // (that wiring lives in SignalOrchestrator, exercised separately) - confirms
        // the honest "nothing supplied" default rather than a fabricated value.
        Assert.Contains("no news state supplied", result.NewsState, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no calendar state supplied", result.EconomicCalendarState, StringComparison.OrdinalIgnoreCase);
    }
}
