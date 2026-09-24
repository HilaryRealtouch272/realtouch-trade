using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

// A qualified signal that price has not reached yet is alerted and logged as
// Pending; it becomes a real, P&L-bearing position only when price trades
// through its entry, and is "Unfilled" (no win, no loss) if the move is missed
// or the window expires. Nothing before the fill ever counts toward the trade.
public class PendingSignalTests : IDisposable
{
    // Long: entry 100, stop 90 (risk 10) -> TP1 110, TP2 120, TP3 140.
    private static QualificationLogEntry Pending(string direction = "Long", DateTime? qualifiedAt = null)
    {
        var q = qualifiedAt ?? DateTime.UtcNow.AddHours(-1);
        var isLong = direction == "Long";
        return new(
            Id: "p", Symbol: "TEST/USD", Timeframe: "1H", Direction: direction, SetupModel: "RangeBoundaryRejection",
            Grade: "B", Score: 77, Entry: 100m, Stop: isLong ? 90m : 110m,
            Tp1: isLong ? 110m : 90m, Tp2: isLong ? 120m : 80m, Tp3: isLong ? 140m : 60m, RewardToRisk: 2m,
            QualifiedAtUtc: q, TrackingExpiryUtc: q.AddHours(30),
            Status: "Pending", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null);
    }

    private static NormalizedCandle Candle(DateTime open, decimal high, decimal low) => new(
        "TEST/USD", "TEST/USD", "Test", Timeframe.H1, open, open.AddHours(1),
        Open: (high + low) / 2, High: high, Low: low, Close: (high + low) / 2, Volume: null, IsComplete: true,
        ReceivedAtUtc: open.AddHours(1), Quality: DataQuality.Ok);

    [Fact]
    public void ALongFillsWhenACandleTradesDownToItsEntryAndTheFavorableSideOfThatCandleIsNotCredited()
    {
        var entry = Pending();
        // Trades down to 99 (through the 100 entry) and also up to 112, past
        // TP1 (110). Which came first is unknown, so TP1 must NOT be credited.
        var c = Candle(entry.QualifiedAtUtc.AddMinutes(10), high: 112m, low: 99m);

        var result = SignalLogService.ApplyCandleSequence(entry, new[] { c }, DateTime.UtcNow);

        Assert.Equal("Open", result.Status);
        Assert.Equal(c.OpenTimeUtc, result.TriggeredAtUtc);
        Assert.Null(result.Tp1HitAtUtc);
        Assert.Null(result.RealizedR);
    }

    [Fact]
    public void AFillThatAlsoTradesThroughTheStopInTheSameCandleIsAStopOutNotAWin()
    {
        var entry = Pending();
        var c = Candle(entry.QualifiedAtUtc.AddMinutes(10), high: 104m, low: 88m);

        var result = SignalLogService.ApplyCandleSequence(entry, new[] { c }, DateTime.UtcNow);

        Assert.Equal("StoppedOut", result.Status);
        Assert.Equal(-1m, result.RealizedR);
    }

    [Fact]
    public void APendingSignalIsUnfilledWithNoWinOrLossWhenPriceReachesTp1WithoutTouchingTheEntry()
    {
        var entry = Pending();
        var c = Candle(entry.QualifiedAtUtc.AddMinutes(10), high: 111m, low: 102m);

        var result = SignalLogService.ApplyCandleSequence(entry, new[] { c }, DateTime.UtcNow);

        Assert.Equal("Unfilled", result.Status);
        Assert.Null(result.RealizedR);
        Assert.Equal("Unfilled", result.FinalOutcome);
        Assert.NotNull(result.ClosedAtUtc);
        Assert.Null(result.MonetaryPnL);
    }

    [Fact]
    public void AFilledTradeThenResolvesAsUsualOnLaterCandles()
    {
        var entry = Pending();
        var fill = Candle(entry.QualifiedAtUtc.AddHours(1), high: 103m, low: 99.5m);
        var stop = Candle(entry.QualifiedAtUtc.AddHours(2), high: 95m, low: 89m);

        var result = SignalLogService.ApplyCandleSequence(entry, new[] { fill, stop }, DateTime.UtcNow);

        Assert.Equal("StoppedOut", result.Status);
        Assert.Equal(-1m, result.RealizedR);
    }

    [Fact]
    public void PriceActionBeforeTheFillIsNeverCountedOnLaterScans()
    {
        // Already filled at T. A candle from BEFORE T that dipped below the
        // stop, and the fill candle itself (which spiked past TP2), must both
        // be ignored on every later walk - only candles strictly after T count.
        var t = DateTime.UtcNow.AddHours(-3);
        var filled = Pending() with { Status = "Open", TriggeredAtUtc = t, TrackingExpiryUtc = t.AddHours(30) };
        var before = Candle(t.AddHours(-1), high: 101m, low: 80m);
        var fillCandle = Candle(t, high: 125m, low: 99m);
        var after = Candle(t.AddHours(1), high: 104m, low: 97m);

        var result = SignalLogService.ApplyCandleSequence(filled, new[] { before, fillCandle, after }, DateTime.UtcNow);

        Assert.Equal("Open", result.Status);
        Assert.Null(result.Tp2HitAtUtc);
    }

    [Fact]
    public void APendingSignalThatNeverFillsExpiresAsUnfilledNotAsAFlatTrade()
    {
        var entry = Pending(qualifiedAt: DateTime.UtcNow.AddHours(-40)) with { TrackingExpiryUtc = DateTime.UtcNow.AddHours(-10) };

        var result = SignalLogService.ApplyCandleSequence(entry, Array.Empty<NormalizedCandle>(), DateTime.UtcNow);

        Assert.Equal("Unfilled", result.Status);
        Assert.Null(result.RealizedR);
        Assert.Contains("Never filled", result.ClosureReason);
    }

    [Fact]
    public void AShortMirrorsTheLongRules()
    {
        var entry = Pending("Short");

        var filled = SignalLogService.ApplyCandleSequence(entry, new[] { Candle(entry.QualifiedAtUtc.AddMinutes(10), high: 100.5m, low: 88m) }, DateTime.UtcNow);
        var missed = SignalLogService.ApplyCandleSequence(entry, new[] { Candle(entry.QualifiedAtUtc.AddMinutes(10), high: 98m, low: 89m) }, DateTime.UtcNow);

        Assert.Equal("Open", filled.Status);
        Assert.Null(filled.Tp1HitAtUtc); // favorable side of the fill candle not credited
        Assert.Equal("Unfilled", missed.Status);
    }

    [Fact]
    public void WithOnlyALivePriceAPendingSignalFillsAtTheEntryStaysPendingBeforeItAndIsMissedAtTp1()
    {
        var entry = Pending();
        var now = DateTime.UtcNow;

        Assert.Equal("Pending", SignalLogService.ApplyPriceAndExpiry(entry, 105m, now).Status);
        Assert.Equal("Open", SignalLogService.ApplyPriceAndExpiry(entry, 99.5m, now).Status);
        Assert.Equal("Unfilled", SignalLogService.ApplyPriceAndExpiry(entry, 111m, now).Status);
        // Gapped through the entry all the way past the stop: filled, then stopped.
        Assert.Equal("StoppedOut", SignalLogService.ApplyPriceAndExpiry(entry, 85m, now).Status);
    }

    [Fact]
    public void ThePendingEntryNeverGetsStopsTargetsOrExcursionsUntilItFills()
    {
        var entry = Pending();
        var result = SignalLogService.ApplyPriceAndExpiry(entry, 105m, DateTime.UtcNow);

        Assert.Equal("Pending", result.Status);
        Assert.Null(result.MaxFavorableExcursionR);
        Assert.Null(result.MaxAdverseExcursionR);
    }

    private static SignalResult Sig(string direction, decimal livePrice, bool triggered, string grade = "B")
    {
        var isLong = direction == "Long";
        return new(
            SignalId: "s", StrategyVersion: "v", Symbol: "TEST/USD", AssetClass: "fx", DataProvider: "t",
            AnalysisTimeframe: "1H", EntryTimeframe: "1H", Direction: isLong ? SetupDirection.Long : SetupDirection.Short,
            Condition: MarketCondition.Ranging, SetupModel: SetupModelType.RangeBoundaryRejection, Status: SignalState.Triggered,
            SetupQualityScore: 77, Grade: grade, DetectedAtUtc: DateTime.UtcNow, ExpiryUtc: DateTime.UtcNow.AddDays(1),
            LivePrice: livePrice, EntryZoneMin: 99m, EntryZoneMax: 101m, PreferredEntry: 100m, Triggered: triggered,
            Stop: isLong ? 90m : 110m, Tp1: isLong ? 110m : 90m, Tp2: isLong ? 120m : 80m, Tp3: isLong ? 140m : 60m,
            RewardToRisk: 2m, RiskPercent: 0.5m, RiskAmount: 50m, PositionSize: 1m,
            KeyLevels: Array.Empty<KeyLevel>(), ConfluenceFamilies: Array.Empty<ConfluenceFamilyScore>(),
            ReasoningSummary: "t", NewsState: "Unchecked", EconomicCalendarState: "Unavailable", VolatilityState: "n/a",
            SessionState: "n/a", DataFreshness: "t", InvalidationConditions: "t");
    }

    [Theory]
    [InlineData("Long", 105.0, false, "B", true)]    // price above a Long entry: limit still waiting
    [InlineData("Long", 95.0, false, "B", false)]    // price already through the entry without triggering
    [InlineData("Long", 100.0, false, "B", false)]   // exactly at the entry, untriggered: not strictly ahead
    [InlineData("Long", 95.0, true, "B", true)]      // already triggered: always actionable
    [InlineData("Short", 95.0, false, "B", true)]    // price below a Short entry
    [InlineData("Short", 105.0, false, "B", false)]  // already through it
    [InlineData("Long", 105.0, false, "Tracking", false)] // a non-qualifying grade is never actionable
    [InlineData("Short", 95.0, true, "No setup", false)]
    public void ActionableMeansQualifiedAndEitherInTheZoneOrStillAheadOfTheEntry(string direction, double price, bool triggered, string grade, bool expected)
    {
        Assert.Equal(expected, SignalLogService.IsActionable(Sig(direction, (decimal)price, triggered, grade)));
    }

    // ---- service-level: a pending signal is logged, fills, and resolves ----

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "rst-pending-" + Guid.NewGuid().ToString("N"));

    private class FakeHost : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private class NoOpFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private SignalLogService NewService() => new(
        new TelegramNotifier(new NoOpFactory(), new ConfigurationBuilder().Build()),
        new FakeHost { ContentRootPath = _tempRoot }, NullLogger<SignalLogService>.Instance);

    [Fact]
    public async Task AQualifiedButUnreachedSignalIsLoggedAsPendingThenFillsThenResolves()
    {
        var service = NewService();

        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(true, Sig("Long", 105m, triggered: false), null, "TEST/USD", "1H")
        });

        var logged = Assert.Single(service.GetAll());
        Assert.Equal("Pending", logged.Status);
        Assert.Equal("Long", service.GetOpenDirection("TEST/USD", "1H")); // active: blocks a duplicate or opposite signal

        // A later scan (no new qualifying setup) whose candles trade through the entry.
        var fillCandle = Candle(DateTime.UtcNow.AddMinutes(5), high: 103m, low: 99m);
        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(false, null, "No model qualified this scan", "TEST/USD", "1H", LivePrice: 101m, Candles: new[] { fillCandle })
        });
        Assert.Equal("Open", Assert.Single(service.GetAll()).Status);
        Assert.NotNull(service.GetOpenDirection("TEST/USD", "1H"));

        // Then the stop.
        var stopCandle = Candle(DateTime.UtcNow.AddMinutes(10), high: 95m, low: 89m);
        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(false, null, "No model qualified this scan", "TEST/USD", "1H", LivePrice: 90m, Candles: new[] { fillCandle, stopCandle })
        });
        var done = Assert.Single(service.GetAll());
        Assert.Equal("StoppedOut", done.Status);
        Assert.Equal(-1m, done.RealizedR);
        Assert.Null(service.GetOpenDirection("TEST/USD", "1H")); // no longer active
    }

    // ---- withdrawal: the setup stops qualifying before price reaches the entry ----

    private async Task<SignalLogService> ServiceWithAPendingLong()
    {
        var service = NewService();
        await service.RecordAndTrackAsync(new[] { new OrchestratorResult(true, Sig("Long", 105m, triggered: false), null, "TEST/USD", "1H") });
        Assert.Equal("Pending", Assert.Single(service.GetAll()).Status);
        return service;
    }

    [Fact]
    public async Task APendingSignalIsWithdrawnWhenTheEngineRerunsOnGoodDataAndTheSetupNoLongerQualifies()
    {
        var service = await ServiceWithAPendingLong();

        // The engine ran (a live price came back) but nothing qualifies any more.
        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(false, null, "No model qualified this scan - closest was RangeBoundaryRejection at 70/78", "TEST/USD", "1H", LivePrice: 104m)
        });

        var entry = Assert.Single(service.GetAll());
        Assert.Equal("Withdrawn", entry.Status);
        Assert.Null(entry.RealizedR);
        Assert.Contains("No longer qualifies", entry.ClosureReason);
        Assert.Null(service.GetOpenDirection("TEST/USD", "1H")); // released: no longer blocks or tracks
    }

    [Fact]
    public async Task ADataFailureNeverWithdrawsAPendingSignal()
    {
        var service = await ServiceWithAPendingLong();

        // Stale data / an exception carries no live price: nothing can be judged.
        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(false, null, "Data unavailable or stale - refusing to generate a signal on incomplete data", "TEST/USD", "1H")
        });

        Assert.Equal("Pending", Assert.Single(service.GetAll()).Status);
    }

    [Fact]
    public async Task APendingSignalStaysWhileTheSameDirectionStillQualifies()
    {
        var service = await ServiceWithAPendingLong();

        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(true, Sig("Long", 104m, triggered: false), null, "TEST/USD", "1H", LivePrice: 104m)
        });

        Assert.Equal("Pending", Assert.Single(service.GetAll()).Status);
    }

    [Fact]
    public async Task APendingSignalIsWithdrawnWhenTheSetupFlipsToTheOppositeDirection()
    {
        var service = await ServiceWithAPendingLong();

        await service.RecordAndTrackAsync(new[]
        {
            // Price is still ABOVE the Long's entry (so it has not filled); the
            // engine now sees a Short instead.
            new OrchestratorResult(true, Sig("Short", 104m, triggered: false), null, "TEST/USD", "1H", LivePrice: 104m)
        });

        var entry = Assert.Single(service.GetAll());
        Assert.Equal("Withdrawn", entry.Status);
        Assert.Contains("flipped to Short", entry.ClosureReason);
    }

    [Fact]
    public async Task IfPriceAlreadyFilledTheEntryTheFillStandsEvenThoughTheSetupWasWithdrawnInTheSameScan()
    {
        var service = await ServiceWithAPendingLong();

        // Candles show the entry was traded through, AND the setup no longer
        // qualifies. A limit order may well have filled before the scan could
        // know - so it is a position, not a withdrawal.
        var fill = Candle(DateTime.UtcNow.AddMinutes(5), high: 103m, low: 99m);
        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(false, null, "No model qualified this scan", "TEST/USD", "1H", LivePrice: 101m, Candles: new[] { fill })
        });

        Assert.Equal("Open", Assert.Single(service.GetAll()).Status);
    }

    [Fact]
    public async Task AnOpenTradeIsNeverWithdrawnByASetupThatStopsQualifying()
    {
        var service = NewService();
        await service.RecordAndTrackAsync(new[] { new OrchestratorResult(true, Sig("Long", 100m, triggered: true), null, "TEST/USD", "1H") });

        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(false, null, "No model qualified this scan", "TEST/USD", "1H", LivePrice: 101m)
        });

        Assert.Equal("Open", Assert.Single(service.GetAll()).Status);
    }

    [Fact]
    public void TheWithdrawalMessageTellsTheTraderToStandAsideAndIsSafeForMarkdown()
    {
        var entry = Pending() with
        {
            Status = "Withdrawn",
            ClosureReason = "No longer qualifies before the entry was reached: closest was RANGE_BOUNDARY at *70*/78"
        };

        var message = SignalLogService.FormatOutcomeMessage(entry);

        Assert.Contains("SETUP WITHDRAWN", message);
        Assert.Contains("Do not enter", message);
        Assert.Contains("No longer qualifies", message);
        Assert.DoesNotContain("_", message);
        Assert.DoesNotContain("*70*", message);
    }

    [Fact]
    public async Task ASignalWhosePriceHasAlreadyGoneThroughItsEntryIsNotLoggedAtAll()
    {
        var service = NewService();

        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(true, Sig("Long", 95m, triggered: false), null, "TEST/USD", "1H")
        });

        Assert.Empty(service.GetAll());
    }

    [Fact]
    public async Task AnAlreadyTriggeredSignalIsStillLoggedDirectlyAsOpen()
    {
        var service = NewService();

        await service.RecordAndTrackAsync(new[]
        {
            new OrchestratorResult(true, Sig("Long", 100m, triggered: true), null, "TEST/USD", "1H")
        });

        Assert.Equal("Open", Assert.Single(service.GetAll()).Status);
    }

    [Fact]
    public void ThePendingTelegramMessageSaysPlainlyThatTheEntryIsNotReached()
    {
        var pending = TelegramSignalFormatter.Format(Sig("Long", 105m, triggered: false), qualified: true);
        var triggered = TelegramSignalFormatter.Format(Sig("Long", 100m, triggered: true), qualified: true);

        Assert.Contains("PENDING", pending);
        Assert.Contains("entry not reached yet", pending);
        Assert.DoesNotContain("PENDING", triggered);
        Assert.DoesNotContain("_", pending); // Telegram Markdown mode
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }
}
