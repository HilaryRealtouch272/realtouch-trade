using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class CandleQualityCheckerTests
{
    private static NormalizedCandle Candle(DateTime openTime, Timeframe tf = Timeframe.Daily, bool complete = true) =>
        new("TEST/USD", "TESTUSD", "Fixture", tf, openTime, openTime + TimeframeConfig.Duration(tf),
            100, 101, 99, 100, null, complete, DateTime.UtcNow, DataQuality.Ok);

    [Fact]
    public void DropsExactDuplicateCandles()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var raw = new List<NormalizedCandle> { Candle(t0), Candle(t0), Candle(t0.AddDays(1)) };

        var result = CandleQualityChecker.Annotate(raw, Timeframe.Daily, DateTime.UtcNow);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FlagsAGapWhenAnExpectedCandleIsMissing()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var raw = new List<NormalizedCandle> { Candle(t0), Candle(t0.AddDays(1)), Candle(t0.AddDays(5)) };

        var result = CandleQualityChecker.Annotate(raw, Timeframe.Daily, DateTime.UtcNow);

        Assert.Equal(DataQuality.Gap, result[2].Quality);
    }

    [Fact]
    public void FlagsTheLastCandleAsStaleWhenTooOld()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var raw = new List<NormalizedCandle> { Candle(t0), Candle(t0.AddDays(1)) };
        var farFuture = t0.AddDays(30);

        var result = CandleQualityChecker.Annotate(raw, Timeframe.Daily, farFuture);

        Assert.Equal(DataQuality.Stale, result[^1].Quality);
    }

    [Fact]
    public void HasUsableDataFailsWhenLastCandleIsStale()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var raw = new List<NormalizedCandle> { Candle(t0), Candle(t0.AddDays(1)) };
        var annotated = CandleQualityChecker.Annotate(raw, Timeframe.Daily, t0.AddDays(30));

        Assert.False(CandleQualityChecker.HasUsableData(annotated, minimumCompleted: 1));
    }
}
