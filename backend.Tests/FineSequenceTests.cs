using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

// One-minute settlement: the candle a trade is entered in is covered from the
// moment of entry, every minute is applied once, and a restart resumes from the
// watermark. Long fixture: entry 100, stop 90, TP1 110, TP2 120, TP3 140.
public class FineSequenceTests
{
    private static readonly DateTime Q = new(2026, 9, 24, 9, 44, 21, DateTimeKind.Utc);

    private static QualificationLogEntry Entry() => new(
        Id: "t", Symbol: "BTC/USDT", Timeframe: "15m", Direction: "Long", SetupModel: "TrendContinuationPullback",
        Grade: "B", Score: 80, Entry: 100m, Stop: 90m, Tp1: 110m, Tp2: 120m, Tp3: 140m, RewardToRisk: 2m,
        QualifiedAtUtc: Q, TrackingExpiryUtc: Q.AddHours(8),
        Status: "Open", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null,
        Exits: Array.Empty<ExitFill>());

    private static NormalizedCandle Minute(int minutesAfterQ, decimal open, decimal high, decimal low, decimal close)
    {
        var openTime = new DateTime(Q.Year, Q.Month, Q.Day, Q.Hour, Q.Minute, 0, DateTimeKind.Utc).AddMinutes(minutesAfterQ + 1);
        return new NormalizedCandle("BTC/USDT", "BTC-USD", "Test", Timeframe.M15, openTime, openTime.AddMinutes(1),
            open, high, low, close, null, true, openTime.AddMinutes(1), DataQuality.Ok);
    }

    [Fact]
    public void AStopTouchedInTheMinutesRightAfterEntryIsCaughtAndTheCandleIsNotSkipped()
    {
        // The coarse 15m walk never looked at the candle the trade was entered in,
        // so a stop touched there and then recovered was silently missed.
        var candles = new[]
        {
            Minute(0, 100m, 101m, 99m, 100m),
            Minute(1, 100m, 100m, 89m, 95m),   // touches the stop
            Minute(2, 95m, 125m, 94m, 124m)    // recovery that the coarse walk would have seen as a win
        };

        var result = TradeSimulator.ApplyFineSequence(Entry(), candles, Q.AddMinutes(10));

        Assert.Equal("StoppedOut", result.Status);
        Assert.Null(result.Tp1HitAtUtc);
    }

    [Fact]
    public void ReplayingTheSameMinutesTwiceGivesTheSameResultAndTheWatermarkAdvances()
    {
        var candles = new[] { Minute(0, 100m, 105m, 99m, 104m), Minute(1, 104m, 106m, 103m, 105m) };
        var now = Q.AddMinutes(10);

        var first = TradeSimulator.ApplyFineSequence(Entry(), candles, now);
        var second = TradeSimulator.ApplyFineSequence(first, candles, now);

        Assert.Equal("Open", first.Status);
        Assert.Equal(candles[^1].CloseTimeUtc, first.LastProcessedUtc);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AMinuteThatHasNotClosedYetIsNotApplied()
    {
        var candles = new[] { Minute(0, 100m, 100m, 80m, 85m) };

        var result = TradeSimulator.ApplyFineSequence(Entry(), candles, Q.AddSeconds(30));

        Assert.Equal("Open", result.Status);
        Assert.Null(result.LastProcessedUtc);
    }

    [Fact]
    public void ARestartResumesFromTheWatermarkWithoutReapplyingEarlierMinutes()
    {
        var early = Minute(0, 100m, 112m, 99m, 111m); // TP1 reached
        var resumed = TradeSimulator.ApplyFineSequence(Entry(), new[] { early }, Q.AddMinutes(5));
        Assert.Equal("Tp1Hit", resumed.Status);

        // After a restart the full list is handed back, including the old minute.
        var later = Minute(3, 111m, 112m, 89m, 90m); // then the stop
        var final = TradeSimulator.ApplyFineSequence(resumed, new[] { early, later }, Q.AddMinutes(10));

        Assert.Equal("StoppedOut", final.Status);
        Assert.Equal(2, final.Exits!.Count(x => x.Reason is "TP1" or "Stop"));
    }
}
