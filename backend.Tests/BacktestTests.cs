using RealtouchSmartTrade.Api.Backtesting;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class BacktestTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static NormalizedCandle Candle(int index, TimeSpan span, decimal open = 100m, decimal high = 101m, decimal low = 99m, decimal close = 100m) =>
        new("BTC/USDT", "BTC-USD", "Test", Timeframe.M15, T0 + span * index, T0 + span * (index + 1),
            open, high, low, close, 1m, true, T0, DataQuality.Ok);

    [Fact]
    public void TheReplayNeverShowsACandleThatHadNotClosedByTheReplayTime()
    {
        var span = TimeSpan.FromMinutes(15);
        var series = Enumerable.Range(0, 10).Select(i => Candle(i, span)).ToList();
        var replay = new ReplayMarketDataProvider();
        replay.AddSeries("BTC/USDT", Timeframe.M15, series);

        // 40 minutes in: candles 0 and 1 have closed (at 15 and 30 min); candle 2 closes at 45.
        replay.AsOf = T0.AddMinutes(40);
        var visible = replay.GetCandlesAsync("BTC/USDT", "BTC-USD", Timeframe.M15).Result;

        Assert.Equal(2, visible.Count);
        Assert.All(visible, c => Assert.True(c.CloseTimeUtc <= replay.AsOf));
    }

    [Fact]
    public void AggregationOnlyBuildsFullyPopulatedBucketsAndNeverInventsAPartialOne()
    {
        var hour = TimeSpan.FromHours(1);
        // Hours 0-3 form one full 4H bucket; hours 4-5 are a partial bucket.
        var fine = Enumerable.Range(0, 6).Select(i => Candle(i, hour, open: 100 + i, high: 110 + i, low: 90 + i, close: 101 + i)).ToList();

        var h4 = ReplayMarketDataProvider.Aggregate(fine, hour, TimeSpan.FromHours(4), Timeframe.H4);

        var bar = Assert.Single(h4);
        Assert.Equal(100m, bar.Open);
        Assert.Equal(113m, bar.High);
        Assert.Equal(90m, bar.Low);
        Assert.Equal(104m, bar.Close);
    }

    private static QualificationLogEntry Closed(string status, decimal netR, int day) => new(
        Id: Guid.NewGuid().ToString("N"), Symbol: "BTC/USDT", Timeframe: "15m", Direction: "Long", SetupModel: "TrendContinuationPullback",
        Grade: "B", Score: 80, Entry: 100m, Stop: 90m, Tp1: 110m, Tp2: 120m, Tp3: 140m, RewardToRisk: 2m,
        QualifiedAtUtc: T0.AddDays(day), TrackingExpiryUtc: T0.AddDays(day + 1),
        Status: status, Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: T0.AddDays(day).AddHours(2), RealizedR: netR, NetRealizedR: netR);

    [Fact]
    public void StatsCountNetProfitableTradesNotStatusAndFlagSmallSamples()
    {
        var trades = new[] { Closed("Tp3Hit", 3m, 0), Closed("StoppedOut", 0.4m, 1), Closed("StoppedOut", -1m, 2), Closed("StoppedOut", -1m, 3) };

        var s = BacktestStats.Segment("all", trades);

        Assert.Equal(4, s.Trades);
        Assert.Equal(2, s.NetProfitable);            // the +0.4R stopped trade counts as profitable
        Assert.Equal(1, s.Tp3);
        Assert.Equal(3, s.StopTriggered);
        Assert.Equal(0.35, s.ExpectancyR, 3);
        Assert.Equal(3.4 / 2.0, s.ProfitFactor, 3);
        Assert.True(s.Provisional);
        Assert.InRange(s.WilsonLower, 0.0, s.NetProfitableRate);
    }

    [Fact]
    public void MaxDrawdownIsPeakToTroughOfTheCumulativeRCurveInCloseOrder()
    {
        var trades = new[] { Closed("Tp3Hit", 2m, 0), Closed("StoppedOut", -1m, 1), Closed("StoppedOut", -1m, 2), Closed("Tp3Hit", 3m, 3) };

        Assert.Equal(2.0, BacktestStats.Segment("dd", trades).MaxDrawdownR, 3);
    }

    [Fact]
    public void WalkForwardFoldsAreSuccessiveInTimeAndCoverEveryTradeOnce()
    {
        var trades = Enumerable.Range(0, 8).Select(i => Closed("StoppedOut", -1m, i)).ToList();

        var folds = BacktestStats.WalkForward(trades, 4);

        Assert.Equal(4, folds.Count);
        Assert.Equal(8, folds.Sum(f => f.Trades));
        for (var i = 1; i < folds.Count; i++) Assert.True(folds[i].From > folds[i - 1].From);
    }

    [Fact]
    public void TheOutOfSampleSegmentIsStrictlyLaterThanTheInSampleSegment()
    {
        var trades = Enumerable.Range(0, 10).Select(i => Closed("StoppedOut", -1m, i)).ToList();

        var (inSample, outOfSample) = BacktestStats.InOutOfSample(trades, 0.7);

        Assert.Equal(7, inSample.Trades);
        Assert.Equal(3, outOfSample.Trades);
        Assert.True(outOfSample.From > inSample.From);
    }
}
