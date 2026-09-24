using System.Text.Json;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

// Sections 14 to 16: simulated fills, costs, partial exits, gaps and
// same-candle sequencing. EUR/USD: pip = 0.0001.
// Long fixture: entry 1.1000, stop 1.0990 (risk 10 pips = 1R),
// TP1 1.1010 (1R), TP2 1.1020 (2R), TP3 1.1040 (4R). Cost 1.0 + 0.3 = 1.3 pips = 0.13R.
public class TradeSimulatorTests
{
    private static readonly DateTime Qualified = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Qualified.AddHours(6);

    private static QualificationLogEntry Long(string status = "Open", decimal spread = 1.0m, decimal slippage = 0.3m) => new(
        Id: "t", Symbol: "EUR/USD", Timeframe: "1H", Direction: "Long", SetupModel: "TrendContinuationPullback",
        Grade: "B", Score: 80, Entry: 1.1000m, Stop: 1.0990m, Tp1: 1.1010m, Tp2: 1.1020m, Tp3: 1.1040m, RewardToRisk: 2m,
        QualifiedAtUtc: Qualified, TrackingExpiryUtc: Qualified.AddHours(30),
        Status: status, Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null,
        RiskPercent: 0.5m, RiskAmount: 100m, EntrySpreadUnits: spread, SlippageUnits: slippage,
        Exits: Array.Empty<ExitFill>());

    private static NormalizedCandle Candle(DateTime open, TimeSpan span, decimal o, decimal h, decimal l, decimal c) => new(
        "EUR/USD", "EUR/USD", "Test", Timeframe.H1, open, open + span, o, h, l, c, null, true, open + span, DataQuality.Ok);

    private static NormalizedCandle Hour(int minutesAfterQualified, decimal o, decimal h, decimal l, decimal c) =>
        Candle(Qualified.AddMinutes(minutesAfterQualified), TimeSpan.FromHours(1), o, h, l, c);

    private static NormalizedCandle Quarter(DateTime open, decimal o, decimal h, decimal l, decimal c) =>
        Candle(open, TimeSpan.FromMinutes(15), o, h, l, c);

    // ---------------- costs and net results ----------------

    [Fact]
    public void AStopOutIsChargedTheRoundTripCostInPipsAndInR()
    {
        var r = TradeSimulator.ApplyPriceAndExpiry(Long(), 1.0990m, Now);

        Assert.Equal("StoppedOut", r.Status);
        Assert.Equal(-1m, r.RealizedR);          // gross
        Assert.Equal(-1.13m, r.NetRealizedR);    // 1.3 pips of cost = 0.13R
        Assert.Equal(-10m, r.GrossMovementUnits);
        Assert.Equal(1.3m, r.CostMovementUnits);
        Assert.Equal(-11.3m, r.NetMovementUnits);
        Assert.Equal("pips", r.MovementUnitLabel);
        Assert.Equal(-113m, r.MonetaryPnL);      // on NET R, not gross
        Assert.Equal(-0.565m, r.PercentageReturn);
    }

    [Fact]
    public void ATp1PartialThenAStopIsWeightedByWhatWasActuallyExecuted()
    {
        var afterTp1 = TradeSimulator.ApplyPriceAndExpiry(Long(), 1.1010m, Now);
        Assert.Equal("Tp1Hit", afterTp1.Status);
        var tp1Exit = Assert.Single(afterTp1.Exits!);
        Assert.Equal("TP1", tp1Exit.Reason);
        Assert.Equal(0.25m, tp1Exit.Fraction);

        var r = TradeSimulator.ApplyPriceAndExpiry(afterTp1, 1.0990m, Now);

        Assert.Equal("StoppedOut", r.Status);
        Assert.Equal(new[] { "TP1", "Stop" }, r.Exits!.Select(x => x.Reason));
        Assert.Equal(new[] { 0.25m, 0.75m }, r.Exits!.Select(x => x.Fraction));
        Assert.Equal(1m, r.Exits!.Sum(x => x.Fraction));
        Assert.Equal(-0.5m, r.RealizedR);         // 0.25 x (+1R) + 0.75 x (-1R)
        Assert.Equal(-0.63m, r.NetRealizedR);     // minus 0.13R of cost
        Assert.Equal(-5m, r.GrossMovementUnits);  // 0.25 x 10 + 0.75 x (-10)
        Assert.Equal(-6.3m, r.NetMovementUnits);
    }

    [Fact]
    public void ATp3WinBanksAllThreeLegsAndTheirFractionsSumToOne()
    {
        var r = TradeSimulator.ApplyPriceAndExpiry(Long(), 1.1040m, Now);

        Assert.Equal("Tp3Hit", r.Status);
        Assert.Equal(new[] { "TP1", "TP2", "TP3" }, r.Exits!.Select(x => x.Reason));
        Assert.Equal(1m, r.Exits!.Sum(x => x.Fraction));
        Assert.Equal(2.25m, r.RealizedR);         // 0.25 x 1 + 0.50 x 2 + 0.25 x 4
        Assert.Equal(2.12m, r.NetRealizedR);
        Assert.Equal("TP3 Win", r.FinalOutcome);
    }

    [Fact]
    public void AFullWinSmallerThanTheCostIsANetLossNotAWin()
    {
        // A 1 pip stop with targets at 0.5, 1 and 1.5 pips: hitting TP3 is a
        // gross +1.0R (+1 pip) but costs 1.3 pips, so it must read as a net loss.
        var tiny = Long() with { Stop = 1.0999m, Tp1 = 1.10005m, Tp2 = 1.1001m, Tp3 = 1.10015m };

        var r = TradeSimulator.ApplyPriceAndExpiry(tiny, 1.10015m, Now);

        Assert.Equal("Tp3Hit", r.Status);
        Assert.Equal(1.0m, r.RealizedR);
        Assert.Equal(-0.3m, r.NetRealizedR);      // 1.0R gross - 1.3R of cost
        Assert.True(r.MonetaryPnL < 0m);
    }

    // ---------------- gaps ----------------

    [Fact]
    public void AStopThatTheMarketGappedThroughFillsAtTheOpenNotAtItsLevel()
    {
        // The candle OPENS at 1.0980, already below the 1.0990 stop.
        var gap = Hour(60, 1.0980m, 1.0985m, 1.0975m, 1.0978m);

        var r = TradeSimulator.ApplyCandleSequence(Long(), new[] { gap }, Now);

        var stop = r.Exits!.Single(x => x.Reason == "Stop");
        Assert.Equal(1.0980m, stop.FillPrice);
        Assert.Equal(1.0990m, stop.LevelPrice);
        Assert.Equal(-2m, r.RealizedR);           // (1.0980 - 1.1000) / 0.0010
        Assert.Contains("gapped", r.ClosureReason);
    }

    [Fact]
    public void ATargetIsALimitOrderSoAGapPastItStillFillsAtTheLevel()
    {
        var gapUp = Hour(60, 1.1015m, 1.1018m, 1.1012m, 1.1016m);

        var r = TradeSimulator.ApplyCandleSequence(Long(), new[] { gapUp }, Now);

        var tp1 = r.Exits!.Single(x => x.Reason == "TP1");
        Assert.Equal(1.1010m, tp1.FillPrice);     // never better than the limit
    }

    // ---------------- same-candle sequencing ----------------

    [Fact]
    public void AStopAndATargetInOneCandleWithNoFinerDataIsStopFirstAndFlaggedUncertain()
    {
        var both = Hour(60, 1.1002m, 1.1015m, 1.0985m, 1.1000m);

        var r = TradeSimulator.ApplyCandleSequence(Long(), new[] { both }, Now);

        Assert.Equal("StoppedOut", r.Status);
        Assert.Equal(-1m, r.RealizedR);           // the favourable target is never assumed first
        Assert.True(r.IntrabarSequenceUncertain);
    }

    [Fact]
    public void FinerCandlesSettleWhichCameFirstWhenTheTargetWasReachedBeforeTheStop()
    {
        var both = Hour(60, 1.1002m, 1.1015m, 1.0985m, 1.0990m);
        var t0 = both.OpenTimeUtc;
        var fine = new[]
        {
            Quarter(t0, 1.1002m, 1.1004m, 1.1000m, 1.1003m),
            Quarter(t0.AddMinutes(15), 1.1003m, 1.1012m, 1.1002m, 1.1011m),  // TP1 (1.1010) first
            Quarter(t0.AddMinutes(30), 1.1011m, 1.1011m, 1.0985m, 1.0988m),  // then the stop
            Quarter(t0.AddMinutes(45), 1.0988m, 1.0990m, 1.0985m, 1.0989m)
        };

        var r = TradeSimulator.ApplyCandleSequence(Long(), new[] { both }, Now, fine);

        Assert.Equal("StoppedOut", r.Status);
        Assert.Equal(new[] { "TP1", "Stop" }, r.Exits!.Select(x => x.Reason));
        Assert.Equal(-0.5m, r.RealizedR);         // TP1 banked before the stop
        Assert.False(r.IntrabarSequenceUncertain);
    }

    [Fact]
    public void FinerCandlesSettleItTheOtherWayWhenTheStopCameFirst()
    {
        var both = Hour(60, 1.1002m, 1.1015m, 1.0985m, 1.1012m);
        var t0 = both.OpenTimeUtc;
        var fine = new[]
        {
            Quarter(t0, 1.1002m, 1.1003m, 1.0985m, 1.0988m),                  // stop first
            Quarter(t0.AddMinutes(15), 1.0988m, 1.1000m, 1.0986m, 1.0999m),
            Quarter(t0.AddMinutes(30), 1.0999m, 1.1008m, 1.0998m, 1.1006m),
            Quarter(t0.AddMinutes(45), 1.1006m, 1.1015m, 1.1004m, 1.1012m)    // target only later
        };

        var r = TradeSimulator.ApplyCandleSequence(Long(), new[] { both }, Now, fine);

        Assert.Equal("StoppedOut", r.Status);
        Assert.Equal(-1m, r.RealizedR);
        Assert.False(r.IntrabarSequenceUncertain);
    }

    [Fact]
    public void AnIncompleteFinerTilingCannotSettleItSoItStaysUncertain()
    {
        var both = Hour(60, 1.1002m, 1.1015m, 1.0985m, 1.1000m);
        var t0 = both.OpenTimeUtc;
        var onlyTwoOfFour = new[]
        {
            Quarter(t0, 1.1002m, 1.1012m, 1.1000m, 1.1011m),
            Quarter(t0.AddMinutes(15), 1.1011m, 1.1012m, 1.0985m, 1.0990m)
        };

        var r = TradeSimulator.ApplyCandleSequence(Long(), new[] { both }, Now, onlyTwoOfFour);

        Assert.True(r.IntrabarSequenceUncertain);
        Assert.Equal(-1m, r.RealizedR);
    }

    [Fact]
    public void ACandleThatOnlyTouchesTheStopOrOnlyATargetIsNeverFlaggedUncertain()
    {
        var stopOnly = TradeSimulator.ApplyCandleSequence(Long(), new[] { Hour(60, 1.0995m, 1.0998m, 1.0985m, 1.0988m) }, Now);
        var targetOnly = TradeSimulator.ApplyCandleSequence(Long(), new[] { Hour(60, 1.1005m, 1.1015m, 1.1002m, 1.1012m) }, Now);

        Assert.False(stopOnly.IntrabarSequenceUncertain);
        Assert.False(targetOnly.IntrabarSequenceUncertain);
    }

    // ---------------- time exits and legacy rows ----------------

    [Fact]
    public void AnExpiredTradeExitsAtTheLatestMarketPriceNotAssumedFlat()
    {
        var expired = Long() with { TrackingExpiryUtc = Qualified.AddHours(2) };
        var drift = Hour(30, 1.1002m, 1.1005m, 1.0995m, 1.1004m);

        var r = TradeSimulator.ApplyCandleSequence(expired, new[] { drift }, Now);

        Assert.Equal("Expired", r.Status);
        var exit = Assert.Single(r.Exits!);
        Assert.Equal("Time exit", exit.Reason);
        Assert.Equal(1.1004m, exit.FillPrice);
        Assert.Equal(0.4m, r.RealizedR);          // (1.1004 - 1.1000) / 0.0010
        Assert.Equal("Rule-Based Exit", r.FinalOutcome);
    }

    [Fact]
    public void AnExpiredTradeWithNoPriceAtAllIsTreatedAsFlat()
    {
        var expired = Long() with { TrackingExpiryUtc = Qualified.AddHours(2) };

        var r = TradeSimulator.ApplyPriceAndExpiry(expired, null, Now);

        Assert.Equal("Expired", r.Status);
        Assert.Equal(0m, r.RealizedR);
    }

    [Fact]
    public void RowsPersistedBeforeExitsWereRecordedKeepTheirBankedLegs()
    {
        // Old rows only know Tp1HitAtUtc; a later stop must still credit that 25% leg.
        var legacy = Long("Tp1Hit") with { Tp1HitAtUtc = Qualified.AddHours(1), Exits = null };

        var r = TradeSimulator.ApplyPriceAndExpiry(legacy, 1.0990m, Now);

        Assert.Equal(new[] { "TP1", "Stop" }, r.Exits!.Select(x => x.Reason));
        Assert.Equal(-0.5m, r.RealizedR);
    }

    [Fact]
    public void AnInstrumentWithNoConfiguredUnitsGetsRAndNoFabricatedPips()
    {
        var unknown = Long() with { Symbol = "UNCONFIGURED/XXX" };

        var r = TradeSimulator.ApplyPriceAndExpiry(unknown, 1.0990m, Now);

        Assert.Equal(-1m, r.RealizedR);
        Assert.Null(r.GrossMovementUnits);
        Assert.Null(r.NetMovementUnits);
        Assert.Null(r.MovementUnitLabel);
    }

    [Fact]
    public void ExitFillsSurviveAJsonRoundTrip()
    {
        var r = TradeSimulator.ApplyPriceAndExpiry(TradeSimulator.ApplyPriceAndExpiry(Long(), 1.1010m, Now), 1.0990m, Now);

        var back = JsonSerializer.Deserialize<QualificationLogEntry>(JsonSerializer.Serialize(r))!;

        Assert.Equal(r.Exits!.Count, back.Exits!.Count);
        Assert.Equal(r.Exits[1].FillPrice, back.Exits[1].FillPrice);
        Assert.Equal(r.NetRealizedR, back.NetRealizedR);
    }
}
