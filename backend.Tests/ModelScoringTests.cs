using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class ModelScoringTests
{
    [Fact]
    public void GradeForRespectsEachModelsOwnThresholdNotAFixedSeventyFive()
    {
        // Section 11, stated explicitly in the brief: "For models with a
        // threshold of 78, scores from 75 to 77 remain Watchlist [Tracking]."
        // A fixed >=75 => B rule (the old shared ConfluenceScorer's rule)
        // would wrongly grade a 76 as B for a threshold-78 model.
        Assert.Equal("Tracking", ModelScoring.GradeFor(76, threshold: 78));
        Assert.Equal("Tracking", ModelScoring.GradeFor(77, threshold: 78));
        Assert.Equal("B", ModelScoring.GradeFor(78, threshold: 78));
        Assert.Equal("B", ModelScoring.GradeFor(84, threshold: 78));
        Assert.Equal("A", ModelScoring.GradeFor(85, threshold: 78));
        Assert.Equal("A+", ModelScoring.GradeFor(90, threshold: 78));

        // A threshold-75 model reaches B one point earlier.
        Assert.Equal("Tracking", ModelScoring.GradeFor(74, threshold: 75));
        Assert.Equal("B", ModelScoring.GradeFor(75, threshold: 75));
    }

    [Fact]
    public void GradeForBelowSixtyFiveIsAlwaysNoSetupRegardlessOfThreshold()
    {
        Assert.Equal("No setup", ModelScoring.GradeFor(64, threshold: 75));
        Assert.Equal("No setup", ModelScoring.GradeFor(64, threshold: 78));
    }

    [Fact]
    public void TrendContinuationBestCaseEvidenceScoresAtLeastNinety()
    {
        var result = ModelScoring.ScoreTrendContinuation(
            HtfAlignment.Aligned, locationQualified: true, zoneSource: "WillisZone", liquidityEventPresent: true,
            entryStructureEvent: StructureEventType.BullishChoch, displacementAtEntry: true, rewardToRisk: 3.5m,
            calendarVeto: CalendarVetoState.NoVeto, newsCatalyst: NewsCatalystState.Aligned);

        Assert.True(result.TotalScore >= 90, $"Expected >=90, got {result.TotalScore}");
        Assert.Equal("A+", result.Grade);
    }

    [Fact]
    public void TrendContinuationWorstCaseEvidenceScoresNoSetup()
    {
        var result = ModelScoring.ScoreTrendContinuation(
            HtfAlignment.Conflicting, locationQualified: false, zoneSource: null, liquidityEventPresent: false,
            entryStructureEvent: null, displacementAtEntry: false, rewardToRisk: null,
            calendarVeto: null, newsCatalyst: null);

        // Only the mandatory "Trending Market Condition quality" family
        // (10 pts) can ever be nonzero here - well under 65.
        Assert.True(result.TotalScore < 65, $"Expected <65, got {result.TotalScore}");
        Assert.Equal("No setup", result.Grade);
    }

    [Fact]
    public void BreakoutAndRetestUsesTheSeventyFiveThreshold()
    {
        var strongEvidence = new BreakoutEvidence(
            BreakoutEventFound: true, BreakoutCandleTimeUtc: DateTime.UtcNow, BreakoutBodyRatio: 1m,
            BreakoutAtrMultiple: 1m, RetestReached: true, ContinuationConfirmed: true, ClearSpaceToOpposingLiquidity: true);

        var result = ModelScoring.ScoreBreakoutAndRetest(strongEvidence, displacementAtEntry: true, rewardToRisk: 3m,
            calendarVeto: CalendarVetoState.NoVeto, newsCatalyst: NewsCatalystState.Aligned);

        Assert.True(result.TotalScore >= 75, $"Expected >=75, got {result.TotalScore}");
        Assert.True(result.Grade is "A+" or "A" or "B");
    }

    [Fact]
    public void BreakoutAndRetestWithNoEventFoundScoresZeroOnItsCoreFamilies()
    {
        var noEvidence = new BreakoutEvidence(false, null, 0, 0, false, false, false);

        var result = ModelScoring.ScoreBreakoutAndRetest(noEvidence, displacementAtEntry: false, rewardToRisk: null, calendarVeto: null, newsCatalyst: null);

        Assert.Equal("No setup", result.Grade);
    }

    [Fact]
    public void LiquiditySweepReversalUsesTheSeventyEightThreshold()
    {
        var strongEvidence = new ReversalEvidence(
            ChochFound: true, ChochCandleTimeUtc: DateTime.UtcNow, ReversalLocationSignificant: true,
            SweepConfirmed: true, SweepQuality: 1m, CloseBackThroughSweptLevel: true, ControlledRetestReached: true);

        var result = ModelScoring.ScoreLiquiditySweepReversal(strongEvidence, reversalZoneEvidence: true, rewardToRisk: 3m,
            calendarVeto: CalendarVetoState.NoVeto, newsCatalyst: NewsCatalystState.Aligned);

        Assert.True(result.TotalScore >= 78, $"Expected >=78, got {result.TotalScore}");
    }

    [Fact]
    public void RangeBoundaryRejectionUsesTheSeventyEightThreshold()
    {
        var strongEvidence = new RangeEvidence(
            RangeEstablished: true, RangeQualityRatio: 1m, NearBoundaryNotEquilibrium: true,
            BoundarySweepOrRejection: true, LowerTimeframeConfirmation: true, SpaceToEquilibriumOrOppositeBoundary: true);

        var result = ModelScoring.ScoreRangeBoundaryRejection(strongEvidence, rewardToRisk: 3m,
            calendarVeto: CalendarVetoState.NoVeto, newsCatalyst: NewsCatalystState.Aligned);

        Assert.True(result.TotalScore >= 78, $"Expected >=78, got {result.TotalScore}");
    }

    [Fact]
    public void EveryModelsFamilyWeightsSumToExactlyOneHundred()
    {
        // Section 6: "Every strategy receives a separately normalised score
        // from 0 to 100." Verified against each model's own best-case call
        // (every family maxed) rather than trusting the weight literals by
        // inspection alone.
        var trend = ModelScoring.ScoreTrendContinuation(HtfAlignment.Aligned, true, "WillisZone", true,
            StructureEventType.BullishChoch, true, 3.5m, CalendarVetoState.NoVeto, NewsCatalystState.Aligned);
        Assert.Equal(100, trend.Families.Sum(f => f.MaxPoints));

        var breakout = ModelScoring.ScoreBreakoutAndRetest(
            new BreakoutEvidence(true, DateTime.UtcNow, 1m, 1m, true, true, true), true, 3.5m, CalendarVetoState.NoVeto, NewsCatalystState.Aligned);
        Assert.Equal(100, breakout.Families.Sum(f => f.MaxPoints));

        var reversal = ModelScoring.ScoreLiquiditySweepReversal(
            new ReversalEvidence(true, DateTime.UtcNow, true, true, 1m, true, true), true, 3.5m, CalendarVetoState.NoVeto, NewsCatalystState.Aligned);
        Assert.Equal(100, reversal.Families.Sum(f => f.MaxPoints));

        var range = ModelScoring.ScoreRangeBoundaryRejection(
            new RangeEvidence(true, 1m, true, true, true, true), 3.5m, CalendarVetoState.NoVeto, NewsCatalystState.Aligned);
        Assert.Equal(100, range.Families.Sum(f => f.MaxPoints));
    }
}
