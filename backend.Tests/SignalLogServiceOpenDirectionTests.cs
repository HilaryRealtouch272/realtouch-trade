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

// Real construction of SignalLogService (not just its internal pure
// functions) - needed to exercise RecordIfNewLocked/GetOpenDirection, which
// depend on its own in-memory _openKeyToEntryId state, not just one entry
// in isolation.
public class SignalLogServiceOpenDirectionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "rst-test-" + Guid.NewGuid().ToString("N"));

    private class FakeHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private SignalLogService NewService() => new(
        new TelegramNotifier(new NoOpHttpClientFactory(), new ConfigurationBuilder().Build()),
        new FakeHostEnvironment { ContentRootPath = _tempRoot },
        NullLogger<SignalLogService>.Instance);

    private class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static OrchestratorResult QualifyingResult(string symbol, string timeframe, string direction)
    {
        var isLong = direction == "Long";
        var signal = new SignalResult(
            SignalId: $"{symbol}:{timeframe}:test", StrategyVersion: "v1-test", Symbol: symbol, AssetClass: "FX",
            DataProvider: "Twelve Data", AnalysisTimeframe: timeframe, EntryTimeframe: timeframe,
            Direction: isLong ? SetupDirection.Long : SetupDirection.Short, Condition: MarketCondition.TrendingBullish,
            SetupModel: SetupModelType.TrendContinuationPullback, Status: SignalState.Triggered,
            SetupQualityScore: 76, Grade: "B", DetectedAtUtc: DateTime.UtcNow, ExpiryUtc: DateTime.UtcNow.AddDays(1),
            LivePrice: 100m, EntryZoneMin: 99m, EntryZoneMax: 101m, PreferredEntry: 100m,
            Stop: isLong ? 90m : 110m, Tp1: isLong ? 110m : 90m, Tp2: isLong ? 120m : 80m, Tp3: isLong ? 140m : 60m,
            RewardToRisk: 2m, RiskPercent: 0.5m, PositionSize: 1m,
            KeyLevels: Array.Empty<KeyLevel>(), ConfluenceFamilies: Array.Empty<ConfluenceFamilyScore>(),
            ReasoningSummary: "test", NewsState: "Unchecked", EconomicCalendarState: "Unavailable",
            VolatilityState: "Unavailable", SessionState: "Unavailable", DataFreshness: "test",
            InvalidationConditions: "test");
        return new OrchestratorResult(Success: true, Signal: signal, Reason: null, InstrumentSymbol: symbol, Timeframe: timeframe);
    }

    [Fact]
    public async Task GetOpenDirectionReturnsNullWhenNothingIsOpen()
    {
        var service = NewService();

        Assert.Null(service.GetOpenDirection("GBP/USD", "15m"));
    }

    [Fact]
    public async Task GetOpenDirectionReflectsTheOpenTradeAndBlocksAnOppositeDirectionNewEntry()
    {
        var service = NewService();
        await service.RecordAndTrackAsync(new[] { QualifyingResult("GBP/USD", "15m", "Short") });

        Assert.Equal("Short", service.GetOpenDirection("GBP/USD", "15m"));

        // A new, opposite-direction qualifying result for the SAME
        // symbol+timeframe must not create a second tracked entry while the
        // original is still open - matches RecordIfNewLocked's own
        // symbol+timeframe (not direction-specific) open-trade key.
        await service.RecordAndTrackAsync(new[] { QualifyingResult("GBP/USD", "15m", "Long") });

        Assert.Single(service.GetAll());
        Assert.Equal("Short", service.GetOpenDirection("GBP/USD", "15m"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }
}
