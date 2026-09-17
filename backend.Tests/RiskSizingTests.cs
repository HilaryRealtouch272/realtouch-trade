using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class RiskSizingTests
{
    [Fact]
    public void DefaultRiskPercentMatchesGradeTable()
    {
        Assert.Equal(1.25m, RiskSizing.DefaultRiskPercent("A+"));
        Assert.Equal(1.0m, RiskSizing.DefaultRiskPercent("A"));
        Assert.Equal(0.5m, RiskSizing.DefaultRiskPercent("B"));
        Assert.Equal(0m, RiskSizing.DefaultRiskPercent("Watchlist"));
    }

    [Fact]
    public void NeverExceedsTheHardTwoPercentCapEvenWhenRequestedHigher()
    {
        var result = RiskSizing.Compute(10000m, requestedRiskPercent: 10m, grade: "A+", entry: 100m, stop: 95m, isFx: false);

        Assert.Equal(RiskSizing.HardCapPercent, result.RiskPercentUsed);
        Assert.Equal(200m, result.RiskAmount); // 2% of 10000
    }

    [Fact]
    public void UsesGradeDefaultWhenNoExplicitRiskPercentRequested()
    {
        var result = RiskSizing.Compute(10000m, requestedRiskPercent: 0m, grade: "B", entry: 100m, stop: 95m, isFx: false);

        Assert.Equal(0.5m, result.RiskPercentUsed);
        Assert.Equal(50m, result.RiskAmount);
    }

    [Fact]
    public void PositionSizeIsRiskAmountDividedByPriceDistance()
    {
        var result = RiskSizing.Compute(10000m, requestedRiskPercent: 1m, grade: "A", entry: 100m, stop: 90m, isFx: false);

        // 1% of 10000 = 100 risk amount; distance = 10; size = 10.
        Assert.Equal(10m, result.PositionSize);
    }

    [Fact]
    public void FeeAndSlippageHaircutReducesEffectivePositionSize()
    {
        var withoutCosts = RiskSizing.Compute(10000m, 1m, "A", 100m, 90m, false);
        var withCosts = RiskSizing.Compute(10000m, 1m, "A", 100m, 90m, false, feePercent: 5m, slippagePercent: 5m);

        Assert.True(withCosts.PositionSize < withoutCosts.PositionSize);
    }

    [Fact]
    public void ZeroStopDistanceProducesZeroSizeRatherThanDividingByZero()
    {
        var result = RiskSizing.Compute(10000m, 1m, "A", 100m, 100m, false);

        Assert.Equal(0m, result.PositionSize);
    }
}

public class PortfolioSafeguardsTests
{
    [Fact]
    public void BlocksNewPositionWhenAggregateRiskWouldExceedLimit()
    {
        var result = PortfolioSafeguards.CheckAggregateRisk(currentOpenRiskPercent: 4m, newRiskPercent: 2m, maxAggregateRiskPercent: 5m);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void AllowsNewPositionWithinAggregateRiskLimit()
    {
        var result = PortfolioSafeguards.CheckAggregateRisk(currentOpenRiskPercent: 2m, newRiskPercent: 1m, maxAggregateRiskPercent: 5m);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void BlocksTradingOnceDailyLossLimitReached()
    {
        var result = PortfolioSafeguards.CheckDailyLoss(todaysRealizedLossPercent: -3m, dailyLossLimitPercent: 3m);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void BlocksWhenAtMaxSimultaneousPositions()
    {
        var result = PortfolioSafeguards.CheckMaxSimultaneousPositions(currentOpenPositions: 5, maxSimultaneousPositions: 5);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void BlocksAnExactDuplicateSignal()
    {
        var result = PortfolioSafeguards.CheckDuplicateSignal(new[] { "gold-w-2026-01-01" }, "gold-w-2026-01-01");

        Assert.False(result.Allowed);
    }

    [Fact]
    public void BlocksCorrelatedExposureWithinTheSameGroup()
    {
        var groups = new Dictionary<string, string[]> { ["usd-majors"] = new[] { "EUR/USD", "GBP/USD" } };

        var result = PortfolioSafeguards.CheckCorrelatedExposure(new[] { "EUR/USD" }, "GBP/USD", groups);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void AllowsUncorrelatedSymbols()
    {
        var groups = new Dictionary<string, string[]> { ["usd-majors"] = new[] { "EUR/USD", "GBP/USD" } };

        var result = PortfolioSafeguards.CheckCorrelatedExposure(new[] { "BTC/USDT" }, "GBP/USD", groups);

        Assert.True(result.Allowed);
    }
}
