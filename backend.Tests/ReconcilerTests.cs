using Microsoft.Extensions.Logging.Abstractions;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class ReconcilerTests
{
    private static readonly DateTime Q = new(2026, 9, 24, 9, 44, 21, DateTimeKind.Utc);

    private class FakeSource(IReadOnlyList<NormalizedCandle> candles) : IFineCandleSource
    {
        public Task<IReadOnlyList<NormalizedCandle>> GetOneMinuteAsync(string symbol, DateTime sinceUtc) => Task.FromResult(candles);
    }

    private static QualificationLogEntry Open() => new(
        Id: "t", Symbol: "BTC/USDT", Timeframe: "15m", Direction: "Long", SetupModel: "TrendContinuationPullback",
        Grade: "B", Score: 80, Entry: 100m, Stop: 90m, Tp1: 110m, Tp2: 120m, Tp3: 140m, RewardToRisk: 2m,
        QualifiedAtUtc: Q, TrackingExpiryUtc: Q.AddHours(8),
        Status: "Open", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null,
        Exits: Array.Empty<ExitFill>());

    private static NormalizedCandle Minute(int after, decimal high, decimal low, decimal close)
    {
        var open = new DateTime(Q.Year, Q.Month, Q.Day, Q.Hour, Q.Minute, 0, DateTimeKind.Utc).AddMinutes(after + 1);
        return new NormalizedCandle("BTC/USDT", "BTC-USD", "Test", Timeframe.M15, open, open.AddMinutes(1),
            100m, high, low, close, null, true, open.AddMinutes(1), DataQuality.Ok);
    }

    private static TradeReconciler Reconciler(params NormalizedCandle[] candles) =>
        new(new FakeSource(candles), NullLogger<TradeReconciler>.Instance);

    [Fact]
    public async Task ATradeThatIsStillOpenButWasStoppedInTheMarketIsTheDiscrepancyItExistsToCatch()
    {
        // The missed-stop case from the review: price crossed the stop, the tracker never saw it.
        var tracker = Open();
        var reconciler = Reconciler(Minute(0, 101m, 99m, 100m), Minute(1, 100m, 88m, 89m));

        var report = await reconciler.ReconcileAsync(new[] { tracker }, Q.AddMinutes(20), TimeSpan.FromHours(24));

        var finding = Assert.Single(report.Findings);
        Assert.Equal("StoppedOut", finding.ReplayStatus);
        Assert.Contains("still shows Open", finding.Discrepancy);
    }

    [Fact]
    public async Task ATradeTheTrackerRecordedCorrectlyProducesNoFinding()
    {
        var candles = new[] { Minute(0, 101m, 99m, 100m), Minute(1, 100m, 88m, 89m) };
        var recorded = TradeSimulator.ApplyFineSequence(Open(), candles, Q.AddMinutes(20));
        Assert.Equal("StoppedOut", recorded.Status);

        var report = await Reconciler(candles).ReconcileAsync(new[] { recorded }, Q.AddMinutes(30), TimeSpan.FromHours(24));

        Assert.Equal(1, report.Checked);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task AMarketWithNoFineFeedIsSkippedAndCountedNeverAssumedCorrect()
    {
        var report = await Reconciler().ReconcileAsync(new[] { Open() }, Q.AddMinutes(20), TimeSpan.FromHours(24));

        Assert.Equal(0, report.Checked);
        Assert.Equal(1, report.Skipped);
    }

    [Fact]
    public async Task OldClosedTradesAreNotReplayed()
    {
        var candles = new[] { Minute(0, 101m, 99m, 100m), Minute(1, 100m, 88m, 89m) };
        var closed = TradeSimulator.ApplyFineSequence(Open(), candles, Q.AddMinutes(20));

        var report = await Reconciler(candles).ReconcileAsync(new[] { closed }, Q.AddDays(3), TimeSpan.FromHours(24));

        Assert.Equal(0, report.Checked);
    }

    [Fact]
    public void TheReplayCopyStartsCleanSoItDependsOnTheMarketAlone()
    {
        var candles = new[] { Minute(0, 112m, 99m, 111m) };
        var done = TradeSimulator.ApplyFineSequence(Open(), candles, Q.AddMinutes(20));
        Assert.Equal("Tp1Hit", done.Status);

        var reset = TradeSimulator.ResetForReplay(done);

        Assert.Equal("Open", reset.Status);
        Assert.Null(reset.LastProcessedUtc);
        Assert.Empty(reset.Exits!);
        Assert.Null(reset.Tp1HitAtUtc);
    }
}
