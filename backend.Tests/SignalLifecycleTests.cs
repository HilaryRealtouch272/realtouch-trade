using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class SignalLifecycleTests
{
    private static TradePlan LongPlan(bool triggered, DateTime expiryUtc, decimal invalidation = 98m, decimal stop = 95m) => new(
        new EntryPlan(98m, 102m, 100m, 100m, null, Timeframe.Daily, expiryUtc, invalidation, triggered, "OrderBlock"),
        new StopPlan(stop, "test stop", true),
        new TargetPlan(105m, "1R", 110m, "2R", 115m, "3R", 0.25m, 0.50m, 0.25m),
        RewardToRisk: 3m
    );

    [Fact]
    public void InitialStateIsWatchlistWhenScoreMeetsWatchlistThreshold()
    {
        Assert.Equal(SignalState.Watchlist, SignalLifecycle.InitialState(70));
        Assert.Equal(SignalState.Scanning, SignalLifecycle.InitialState(50));
    }

    [Fact]
    public void WatchlistDoesNotAdvanceUntilQualified()
    {
        var plan = LongPlan(triggered: false, DateTime.UtcNow.AddDays(10));
        var result = SignalLifecycle.Advance(SignalState.Watchlist, plan, isQualified: false, price: 100m, DateTime.UtcNow, "test");
        Assert.Null(result);
    }

    [Fact]
    public void WatchlistAdvancesToArmedOnceQualified()
    {
        var plan = LongPlan(triggered: false, DateTime.UtcNow.AddDays(10));
        var result = SignalLifecycle.Advance(SignalState.Watchlist, plan, isQualified: true, price: 100m, DateTime.UtcNow, "test");
        Assert.NotNull(result);
        Assert.Equal(SignalState.Armed, result!.To);
    }

    [Fact]
    public void ArmedExpiresAfterTheExpiryWindowWithoutTriggering()
    {
        var plan = LongPlan(triggered: false, expiryUtc: DateTime.UtcNow.AddDays(-1));
        var result = SignalLifecycle.Advance(SignalState.Armed, plan, isQualified: true, price: 100m, DateTime.UtcNow, "test");
        Assert.Equal(SignalState.Expired, result!.To);
    }

    [Fact]
    public void ArmedInvalidatesWhenPriceClosesThroughTheInvalidationLevel()
    {
        var plan = LongPlan(triggered: false, DateTime.UtcNow.AddDays(10), invalidation: 98m);
        var result = SignalLifecycle.Advance(SignalState.Armed, plan, isQualified: true, price: 97m, DateTime.UtcNow, "test");
        Assert.Equal(SignalState.Invalidated, result!.To);
    }

    [Fact]
    public void ArmedTriggersWhenEntryPlanReportsTriggered()
    {
        var plan = LongPlan(triggered: true, DateTime.UtcNow.AddDays(10));
        var result = SignalLifecycle.Advance(SignalState.Armed, plan, isQualified: true, price: 100m, DateTime.UtcNow, "test");
        Assert.Equal(SignalState.Triggered, result!.To);
    }

    [Fact]
    public void FullHappyPathReachesTp3WithoutSkippingStates()
    {
        var plan = LongPlan(triggered: true, DateTime.UtcNow.AddDays(10));
        var now = DateTime.UtcNow;

        var t1 = SignalLifecycle.Advance(SignalState.Triggered, plan, true, 100m, now, "test");
        Assert.Equal(SignalState.Active, t1!.To);

        var t2 = SignalLifecycle.Advance(SignalState.Active, plan, true, 105m, now, "test");
        Assert.Equal(SignalState.Tp1Reached, t2!.To);

        var t3 = SignalLifecycle.Advance(SignalState.Tp1Reached, plan, true, 110m, now, "test");
        Assert.Equal(SignalState.Tp2Reached, t3!.To);

        var t4 = SignalLifecycle.Advance(SignalState.Tp2Reached, plan, true, 115m, now, "test");
        Assert.Equal(SignalState.Tp3Reached, t4!.To);
        Assert.True(SignalLifecycle.IsTerminal(t4.To));
    }

    [Fact]
    public void ActiveStopsOutWhenPriceHitsTheStopLevel()
    {
        var plan = LongPlan(triggered: true, DateTime.UtcNow.AddDays(10), stop: 95m);
        var result = SignalLifecycle.Advance(SignalState.Active, plan, true, 94m, DateTime.UtcNow, "test");
        Assert.Equal(SignalState.Stopped, result!.To);
    }

    [Fact]
    public void TerminalStatesNeverAdvanceFurther()
    {
        var plan = LongPlan(triggered: true, DateTime.UtcNow.AddDays(10));
        foreach (var terminal in new[] { SignalState.Stopped, SignalState.Invalidated, SignalState.Expired, SignalState.Cancelled, SignalState.Tp3Reached })
        {
            var result = SignalLifecycle.Advance(terminal, plan, true, 999m, DateTime.UtcNow, "test");
            Assert.Null(result);
        }
    }
}
