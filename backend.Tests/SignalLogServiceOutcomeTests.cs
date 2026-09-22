using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class SignalLogServiceOutcomeTests
{
    // Long fixture: Entry 100, Stop 90 (risk 10) -> Tp1 110 (1R), Tp2 120 (2R), Tp3 140 (4R).
    private static QualificationLogEntry NewEntry(string status, DateTime? tp1HitAtUtc = null, DateTime? tp2HitAtUtc = null) => new(
        Id: "test", Symbol: "TEST/USD", Timeframe: "1H", Direction: "Long", SetupModel: "TrendContinuationPullback",
        Grade: "B", Score: 76, Entry: 100m, Stop: 90m, Tp1: 110m, Tp2: 120m, Tp3: 140m, RewardToRisk: 2m,
        QualifiedAtUtc: DateTime.UtcNow.AddHours(-1), TrackingExpiryUtc: DateTime.UtcNow.AddHours(29),
        Status: status, Tp1HitAtUtc: tp1HitAtUtc, Tp2HitAtUtc: tp2HitAtUtc, ClosedAtUtc: null, RealizedR: null);

    private static NormalizedCandle Candle(DateTime openTimeUtc, decimal open, decimal high, decimal low, decimal close) => new(
        CanonicalSymbol: "TEST/USD", ProviderSymbol: "TEST/USD", Provider: "Test", Timeframe.H1,
        OpenTimeUtc: openTimeUtc, CloseTimeUtc: openTimeUtc.AddHours(1),
        Open: open, High: high, Low: low, Close: close, Volume: null, IsComplete: true,
        ReceivedAtUtc: openTimeUtc.AddHours(1), Quality: DataQuality.Ok);

    [Fact]
    public void JumpingStraightPastTp1ToTp2BackfillsTp1HitAtUtcToo()
    {
        // Regression: a real production entry (GBP/USD 15m) reached
        // Tp2Hit with Tp1HitAtUtc still null - price legitimately crossed
        // TP1 (closer to entry) on its way to TP2, a scan gap just never
        // caught it exactly there. Denying that TP1 credit would silently
        // undercount BlendedRealizedR if this trade later stops out/expires.
        var entry = NewEntry("Open");

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 120m, DateTime.UtcNow);

        Assert.Equal("Tp2Hit", result.Status);
        Assert.NotNull(result.Tp1HitAtUtc);
        Assert.NotNull(result.Tp2HitAtUtc);
    }

    [Fact]
    public void StoppedOutWithNoPriorTargetHitIsAFullMinusOneR()
    {
        var entry = NewEntry("Open");

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 90m, DateTime.UtcNow);

        Assert.Equal("StoppedOut", result.Status);
        Assert.Equal(-1m, result.RealizedR);
    }

    [Fact]
    public void StoppedOutAfterTp1AlreadyBankedIsABlendedLoss()
    {
        // 25% already closed at TP1 (+1R), remaining 75% stopped out (-1R):
        // 0.25*1 + 0.75*(-1) = -0.5, a smaller loss than a naive flat -1R.
        var entry = NewEntry("Tp1Hit", tp1HitAtUtc: DateTime.UtcNow.AddMinutes(-30));

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 90m, DateTime.UtcNow);

        Assert.Equal("StoppedOut", result.Status);
        Assert.Equal(-0.5m, result.RealizedR);
    }

    [Fact]
    public void StoppedOutAfterTp1AndTp2AlreadyBankedCanStillBeANetWin()
    {
        // 0.25*1 + 0.50*2 + 0.25*(-1) = 0.25 + 1.0 - 0.25 = 1.0R net win,
        // even though the position technically ended in "StoppedOut".
        var entry = NewEntry("Tp2Hit", tp1HitAtUtc: DateTime.UtcNow.AddMinutes(-45), tp2HitAtUtc: DateTime.UtcNow.AddMinutes(-15));

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 90m, DateTime.UtcNow);

        Assert.Equal("StoppedOut", result.Status);
        Assert.Equal(1.0m, result.RealizedR);
    }

    [Fact]
    public void Tp3HitAlwaysBlendsAllThreeTargetsRegardlessOfWhetherTp1Tp2WereSeparatelyRecorded()
    {
        // Price can jump straight past TP1/TP2 to TP3 in one scan without those
        // intermediate hits ever being separately recorded - it still
        // genuinely traversed those levels, so the full blend applies:
        // 0.25*1 + 0.50*2 + 0.25*4 = 2.25R, not just the raw 4R at TP3.
        var entry = NewEntry("Open");

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 140m, DateTime.UtcNow);

        Assert.Equal("Tp3Hit", result.Status);
        Assert.Equal(2.25m, result.RealizedR);
    }

    [Fact]
    public void ExpiredAfterTp1AlreadyBankedKeepsThatProfitInsteadOfReportingFlat()
    {
        // 0.25*1 + 0.75*0 (unknown remainder treated as flat) = 0.25R,
        // not the 0R a naive "expired = flat" would report.
        var entry = NewEntry("Tp1Hit", tp1HitAtUtc: DateTime.UtcNow.AddHours(-2)) with { TrackingExpiryUtc = DateTime.UtcNow.AddMinutes(-1) };

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: null, DateTime.UtcNow);

        Assert.Equal("Expired", result.Status);
        Assert.Equal(0.25m, result.RealizedR);
    }

    [Fact]
    public void ExpiredWithNoPriorTargetHitIsGenuinelyFlat()
    {
        var entry = NewEntry("Open") with { TrackingExpiryUtc = DateTime.UtcNow.AddMinutes(-1) };

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: null, DateTime.UtcNow);

        Assert.Equal("Expired", result.Status);
        Assert.Equal(0m, result.RealizedR);
    }

    [Fact]
    public void MaxFavorableAndAdverseExcursionTrackTheRunningBestAndWorstRegardlessOfFinalOutcome()
    {
        // Long fixture: risk distance is 10 (100 -> 90). A price check at 115
        // is +1.5R favorable; a later dip to 95 is -0.5R adverse, even though
        // the trade is still open and neither is the final result.
        var entry = NewEntry("Open");

        var afterRun = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 115m, DateTime.UtcNow);
        Assert.Equal(1.5m, afterRun.MaxFavorableExcursionR);
        Assert.Equal(1.5m, afterRun.MaxAdverseExcursionR);

        var afterDip = SignalLogService.ApplyPriceAndExpiry(afterRun, livePrice: 95m, DateTime.UtcNow);
        Assert.Equal(1.5m, afterDip.MaxFavorableExcursionR);
        Assert.Equal(-0.5m, afterDip.MaxAdverseExcursionR);
    }

    [Fact]
    public void StoppedOutFullLossPopulatesClosureFieldsWithAConfiguredInstrument()
    {
        // EUR/USD IS configured in InstrumentMetadataCatalog (pip = 0.0001),
        // so gross/net movement and monetary P&L should resolve, unlike the
        // TEST/USD fixture used elsewhere in this file which deliberately
        // exercises the "unconfigured symbol" honest-gap path.
        var entry = NewEntry("Open") with { Symbol = "EUR/USD", RiskAmount = 50m, RiskPercent = 0.5m };

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 90m, DateTime.UtcNow);

        Assert.Equal("StoppedOut", result.Status);
        Assert.Equal("Stopped Out", result.FinalOutcome);
        Assert.Equal(-50m, result.MonetaryPnL);
        Assert.Equal(-0.5m, result.PercentageReturn);
        Assert.NotNull(result.GrossMovementUnits);
        Assert.Equal("pips", result.MovementUnitLabel);
        Assert.NotNull(result.ClosureReason);
        Assert.NotNull(result.HoldingDurationHours);
    }

    [Fact]
    public void ACandleThatTouchesStopThenReversesPastAtargetIsStillHonestlyAStopOut()
    {
        // Regression: a real production trade (XAU/USD 1H, short) genuinely
        // hit its stop, but because the old scalar check only ever compared
        // against the LATEST price, a check that ran after price had
        // already reversed back past a target reported the target hit and
        // never saw the stop. Here: one candle whose Low(=85) crosses the
        // stop(90) and whose High(=125) also crosses TP2(120) - the real
        // stop touch must win, per section 16's conservative same-candle
        // rule (worst extreme for the position resolves first).
        var entry = NewEntry("Open");
        var candles = new[] { Candle(entry.QualifiedAtUtc.AddMinutes(5), open: 100m, high: 125m, low: 85m, close: 122m) };

        var result = SignalLogService.ApplyCandleSequence(entry, candles, DateTime.UtcNow);

        Assert.Equal("StoppedOut", result.Status);
        Assert.Equal(-1m, result.RealizedR);
    }

    [Fact]
    public void ACandleSequenceFindsARealTp3TouchEvenThoughTheCandleClosedWellBelowIt()
    {
        // TP3 (140) is genuinely touched intra-candle (High=141) even though
        // that same candle closes back down at 122 - a close-only check
        // would have missed the touch entirely and likely reported nothing,
        // or a lesser target, hit that scan. Walking High/Low finds it.
        var entry = NewEntry("Open");
        var candles = new[] { Candle(entry.QualifiedAtUtc.AddMinutes(5), open: 100m, high: 141m, low: 99m, close: 122m) };

        var result = SignalLogService.ApplyCandleSequence(entry, candles, DateTime.UtcNow);

        Assert.Equal("Tp3Hit", result.Status);
        Assert.Equal(2.25m, result.RealizedR);
    }

    [Fact]
    public void Tp3HitPopulatesTp3HitAtUtcAndTheWinOutcomeCategory()
    {
        var entry = NewEntry("Open") with { Symbol = "EUR/USD", RiskAmount = 50m, RiskPercent = 0.5m };

        var result = SignalLogService.ApplyPriceAndExpiry(entry, livePrice: 140m, DateTime.UtcNow);

        Assert.Equal("Tp3Hit", result.Status);
        Assert.NotNull(result.Tp3HitAtUtc);
        Assert.Equal("TP3 Win", result.FinalOutcome);
        Assert.Equal(112.5m, result.MonetaryPnL);
    }
}
