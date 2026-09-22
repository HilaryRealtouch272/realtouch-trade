using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class WilsonIntervalTests
{
    [Fact]
    public void TheBriefsOwnSevenTradeBaselineProducesAWideIntervalAndIsProvisional()
    {
        // Section 12's exact baseline: 7 trades, 4 TP3 winners, raw win rate 57.14%.
        var result = WilsonIntervalCalculator.Compute(wins: 4, total: 7);

        Assert.Equal(4.0 / 7, result.PointEstimate, precision: 4);
        // A 7-trade sample's true win rate could honestly be anywhere from
        // about 25% to about 84% - the raw 57.14% figure alone massively
        // overstates how much is actually known here.
        Assert.InRange(result.Lower, 0.24, 0.26);
        Assert.InRange(result.Upper, 0.83, 0.85);
        Assert.True(result.IsProvisional);
        Assert.Contains("30", result.SampleSizeWarning);
    }

    [Fact]
    public void ASampleOfFiftyOrMoreIsNoLongerFlaggedProvisional()
    {
        var result = WilsonIntervalCalculator.Compute(wins: 30, total: 50);

        Assert.False(result.IsProvisional);
    }

    [Fact]
    public void ZeroTradesProducesAZeroedResultRatherThanDividingByZero()
    {
        var result = WilsonIntervalCalculator.Compute(wins: 0, total: 0);

        Assert.Equal(0, result.PointEstimate);
        Assert.Equal(0, result.SampleSize);
    }

    [Fact]
    public void IntervalAlwaysStaysWithinZeroToOne()
    {
        var perfect = WilsonIntervalCalculator.Compute(wins: 10, total: 10);
        Assert.InRange(perfect.Upper, 0, 1);

        var zero = WilsonIntervalCalculator.Compute(wins: 0, total: 10);
        Assert.InRange(zero.Lower, 0, 1);
    }
}
