using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class VtbRulesTests
{
    private static readonly DateTime D0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static NormalizedCandle Daily(int dayIndex, decimal open, decimal high, decimal low, decimal close)
    {
        var openTime = D0.AddDays(dayIndex);
        return new NormalizedCandle("TEST", "TEST", "Test", Timeframe.Daily, openTime, openTime.AddDays(1),
            open, high, low, close, null, true, openTime.AddDays(1), DataQuality.Ok);
    }

    [Fact]
    public void HhNAndLlNExcludeTheCurrentBar()
    {
        // 5 flat bars at 100 (high 101/low 99), then a bar that itself makes a new extreme -
        // HH_5 on that bar must still read the PRIOR 5 bars' high (101), not include its own.
        var candles = new List<NormalizedCandle>();
        for (var i = 0; i < 5; i++) candles.Add(Daily(i, 100, 101, 99, 100));
        candles.Add(Daily(5, 100, 150, 50, 120)); // own high/low is an extreme - must not leak into HH_5/LL_5

        var ind = VtbRules.ComputeIndicators(candles, channelN: 5);
        Assert.Equal(101m, ind[5].HhN);
        Assert.Equal(99m, ind[5].LlN);
    }

    [Fact]
    public void StopAndTargetBothTouchedInTheSameBarCountsAsAStop()
    {
        // 220 flat bars, then a clean long breakout, then one bar whose range covers both
        // the stop and the target - must be recorded as a stop (conservative tie-break).
        var candles = BuildWarmupThenBreakout(out var entryDay);
        var breakoutClose = candles[entryDay].Close;
        var ind = VtbRules.ComputeIndicators(candles, channelN: 20);
        var risk = VtbRules.StopAtrMultiple * ind[entryDay].Atr14;
        var stop = breakoutClose - risk;
        var target = breakoutClose + 2m * risk;

        // Next bar's range swallows both levels.
        candles.Add(Daily(entryDay + 1, breakoutClose, target + 1m, stop - 1m, breakoutClose));

        var (closed, open) = VtbRules.Replay(candles, channelN: 20, rewardToRisk: 2m);
        Assert.Single(closed);
        Assert.Equal(VtbExitReason.Stop, closed[0].Reason);
        Assert.Null(open);
    }

    [Fact]
    public void AnExitAndANewSignalCanHappenOnTheSameBar()
    {
        // The position from the breakout stops out on a bar whose close ALSO qualifies as a
        // fresh short (closes back under the 200-SMA and under LL_20) - a new position must
        // open on that same bar, matching the verified vtb_scanner.py replay() behaviour.
        var candles = BuildWarmupThenBreakout(out var entryDay);
        var breakoutClose = candles[entryDay].Close;
        var ind = VtbRules.ComputeIndicators(candles, channelN: 20);
        var risk = VtbRules.StopAtrMultiple * ind[entryDay].Atr14;
        var stop = breakoutClose - risk;

        // A hard reversal bar: closes well below both SMA200 and the recent low - stops the
        // long out AND is itself a valid fresh short signal.
        var crashClose = stop - risk * 5m;
        candles.Add(Daily(entryDay + 1, breakoutClose, breakoutClose, crashClose - 1m, crashClose));

        var (closed, open) = VtbRules.Replay(candles, channelN: 20, rewardToRisk: 2m);
        Assert.Single(closed);
        Assert.Equal(VtbExitReason.Stop, closed[0].Reason);
        Assert.NotNull(open);
        Assert.Equal(VtbSide.Short, open!.Side);
        Assert.Equal(candles[^1].OpenTimeUtc, open.EntryTimeUtc);
    }

    // 220 flat bars (SMA200 fully warmed, no signal - zero range keeps ATR at 0 so nothing
    // can qualify), then 14 bars with real range to give ATR14 a positive value, then one
    // clean breakout bar. Returns the full series and the breakout bar's index.
    private static List<NormalizedCandle> BuildWarmupThenBreakout(out int entryDay)
    {
        var candles = new List<NormalizedCandle>();
        for (var i = 0; i < 206; i++) candles.Add(Daily(i, 100, 100, 100, 100));
        for (var i = 206; i < 220; i++) candles.Add(Daily(i, 100, 102, 98, 100)); // gives ATR14 > 0
        entryDay = 220;
        candles.Add(Daily(entryDay, 100, 110, 100, 108)); // closes above SMA200 (100) and HH_20 (102)
        return candles;
    }
}
