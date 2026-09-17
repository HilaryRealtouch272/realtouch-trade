using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class PremiumDiscountTests
{
    [Fact]
    public void BuildsARangeFromTheMostRecentImpulseAfterAnUptrend()
    {
        var bars = CandleFixtures.UptrendBars(80);
        var candles = CandleFixtures.FromOhlc(bars, Timeframe.Daily);
        var structure = StructureAnalyzer.Analyze(candles, Timeframe.Daily);

        var range = PremiumDiscount.BuildRange(candles, Timeframe.Daily, structure);

        Assert.NotNull(range);
        Assert.True(range!.High > range.Low);
        Assert.Equal((range.Low + range.High) / 2, range.Equilibrium);
    }

    [Fact]
    public void ClassifiesPriceBelowEquilibriumAsDiscount()
    {
        var range = new DealingRange(100m, 200m, 150m, DateTime.UtcNow, DateTime.UtcNow);

        Assert.Equal(PriceZone.Discount, PremiumDiscount.Classify(range, 120m));
        Assert.Equal(PriceZone.Premium, PremiumDiscount.Classify(range, 180m));
    }

    [Fact]
    public void PreferredRetracementZoneIsNarrowerThanToleratedZone()
    {
        Assert.True(PremiumDiscount.IsInPreferredRetracementZone(0.65m));
        Assert.False(PremiumDiscount.IsInPreferredRetracementZone(0.55m));
        Assert.True(PremiumDiscount.IsInToleratedRetracementZone(0.55m));
        Assert.False(PremiumDiscount.IsInToleratedRetracementZone(0.30m));
    }
}
