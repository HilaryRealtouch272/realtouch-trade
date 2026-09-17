using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class ConfluenceScorerTests
{
    private static ScoringInput BestCaseInput() => new(
        Condition: MarketCondition.TrendingBullish,
        Direction: SetupDirection.Long,
        LocationQualified: true,
        LiquidityEventPresent: true,
        EntryStructureEvent: StructureEventType.BullishChoch,
        ZoneSource: "WillisZone",
        DisplacementAtEntry: true,
        RewardToRisk: 3.5m
    );

    [Fact]
    public void BestCaseInputScoresAtLeastNinety()
    {
        var result = ConfluenceScorer.Score(BestCaseInput());

        Assert.True(result.TotalScore >= 90, $"Expected >= 90, got {result.TotalScore}");
        Assert.Equal("A+", result.Grade);
    }

    [Fact]
    public void WorstCaseInputScoresZeroAndGradesNoSetup()
    {
        var input = new ScoringInput(
            Condition: MarketCondition.Ranging,
            Direction: SetupDirection.Long,
            LocationQualified: false,
            LiquidityEventPresent: false,
            EntryStructureEvent: null,
            ZoneSource: null,
            DisplacementAtEntry: false,
            RewardToRisk: null
        );

        var result = ConfluenceScorer.Score(input);

        Assert.Equal(0, result.TotalScore);
        Assert.Equal("No setup", result.Grade);
    }

    [Fact]
    public void SessionNewsMacroFamilyIsZeroAndExplicitlyLabelledNotEvaluatedWhenNothingSupplied()
    {
        var result = ConfluenceScorer.Score(BestCaseInput());

        var family = result.Families.Single(f => f.Family == "Session, news and macro alignment");
        Assert.Equal(0, family.Points);
        Assert.Contains("no calendar/news state was supplied", family.Basis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionNewsMacroAwardsPointsWhenRealCalendarAndNewsStatesAreSupplied()
    {
        var input = BestCaseInput() with
        {
            CalendarVeto = CalendarVetoState.NoVeto,
            NewsCatalyst = NewsCatalystState.Aligned
        };

        var result = ConfluenceScorer.Score(input);

        var family = result.Families.Single(f => f.Family == "Session, news and macro alignment");
        Assert.Equal(5, family.Points); // 3 (news Aligned) + 2 (no calendar veto) = full 5
    }

    [Fact]
    public void SessionNewsMacroIsZeroedWhenAHardCalendarVetoIsActive()
    {
        var input = BestCaseInput() with
        {
            CalendarVeto = CalendarVetoState.HardVeto,
            NewsCatalyst = NewsCatalystState.Aligned
        };

        var result = ConfluenceScorer.Score(input);

        var family = result.Families.Single(f => f.Family == "Session, news and macro alignment");
        Assert.Equal(0, family.Points);
        Assert.Contains("Hard economic-calendar veto", family.Basis);
    }

    [Fact]
    public void ConflictingHtfOverridesAFullSingleTimeframeMatchToZero()
    {
        var input = BestCaseInput() with { HtfAlignment = HtfAlignment.Conflicting };

        var result = ConfluenceScorer.Score(input);

        var family = result.Families.Single(f => f.Family == "Higher-timeframe context and Market Condition");
        Assert.Equal(0, family.Points);
        Assert.Contains("CONFLICTS", family.Basis);
    }

    [Fact]
    public void AlignedHtfScoresHigherThanNoHtfDataSupplied()
    {
        var aligned = ConfluenceScorer.Score(BestCaseInput() with { HtfAlignment = HtfAlignment.Aligned });
        var unsupplied = ConfluenceScorer.Score(BestCaseInput() with { HtfAlignment = null });

        var alignedFamily = aligned.Families.Single(f => f.Family == "Higher-timeframe context and Market Condition");
        var unsuppliedFamily = unsupplied.Families.Single(f => f.Family == "Higher-timeframe context and Market Condition");
        Assert.True(alignedFamily.Points > unsuppliedFamily.Points);
    }

    [Fact]
    public void IndependentFamilyCountOnlyCountsFamiliesWithPositivePoints()
    {
        var result = ConfluenceScorer.Score(BestCaseInput());

        // Best case: every family scores > 0 except session/news/macro (always 0).
        Assert.Equal(result.Families.Count - 1, result.IndependentFamilyCount);
    }

    [Fact]
    public void NeverAwardsMoreThanEachFamilysMaximum()
    {
        var result = ConfluenceScorer.Score(BestCaseInput());

        Assert.All(result.Families, f => Assert.True(f.Points <= f.MaxPoints));
    }
}
