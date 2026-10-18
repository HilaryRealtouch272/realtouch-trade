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

// Real end-to-end construction (SignalLogService + a real TelegramNotifier hitting a fake
// HTTP handler that always succeeds) so this actually proves a message is or isn't sent,
// not just that the internal bookkeeping looks right in isolation.
public class OutcomeDedupeTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "rst-dedupe-" + Guid.NewGuid().ToString("N"));

    private class FakeHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private class CountingHandler : HttpMessageHandler
    {
        public int Sent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Sent);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
        }
    }

    private class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private (SignalLogService Service, CountingHandler Handler) NewService()
    {
        var handler = new CountingHandler();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:BotToken"] = "test-token", ["Telegram:ChatId"] = "1" })
            .Build();
        var notifier = new TelegramNotifier(new SingleClientFactory(new HttpClient(handler)), config);
        var service = new SignalLogService(notifier, new FakeHostEnvironment { ContentRootPath = _tempRoot }, NullLogger<SignalLogService>.Instance);
        return (service, handler);
    }

    private static OrchestratorResult TriggeredResult(string symbol, string timeframe, string direction = "Long") => new(
        Success: true, Reason: null, InstrumentSymbol: symbol, Timeframe: timeframe,
        Signal: new SignalResult(
            SignalId: $"{symbol}:{timeframe}:test", StrategyVersion: "v1-test", Symbol: symbol, AssetClass: "FX",
            DataProvider: "Twelve Data", AnalysisTimeframe: timeframe, EntryTimeframe: timeframe,
            Direction: SetupDirection.Long, Condition: MarketCondition.TrendingBullish,
            SetupModel: SetupModelType.TrendContinuationPullback, Status: SignalState.Triggered,
            SetupQualityScore: 80, Grade: "A", DetectedAtUtc: DateTime.UtcNow, ExpiryUtc: DateTime.UtcNow.AddDays(1),
            LivePrice: 100m, EntryZoneMin: 99m, EntryZoneMax: 101m, PreferredEntry: 100m, Triggered: true,
            Stop: 90m, Tp1: 110m, Tp2: 120m, Tp3: 140m,
            RewardToRisk: 2m, RiskPercent: 0.5m, RiskAmount: 50m, PositionSize: 1m,
            KeyLevels: Array.Empty<KeyLevel>(), ConfluenceFamilies: Array.Empty<ConfluenceFamilyScore>(),
            ReasoningSummary: "test", NewsState: "Unchecked", EconomicCalendarState: "Unavailable",
            VolatilityState: "Unavailable", SessionState: "Unavailable", DataFreshness: "test",
            InvalidationConditions: "test"));

    [Fact]
    public async Task TheSameResolvedOutcomeIsNeverSentTwice()
    {
        var (service, handler) = NewService();
        // Qualify on 1H (outcome alerts are allowed there), then push price straight to TP3.
        await service.RecordAndTrackAsync(new[] { TriggeredResult("BTC/USDT", "1H") });
        var withPrice = TriggeredResult("BTC/USDT", "1H") with { LivePrice = 145m };

        await service.RecordAndTrackAsync(new[] { withPrice });
        Assert.Equal(1, handler.Sent); // the real event: TP3 hit

        // A later scan recomputes the SAME resolved state (e.g. after a restart replaying
        // candles it already applied) - this must not send a second identical message.
        await service.RecordAndTrackAsync(new[] { withPrice });
        Assert.Equal(1, handler.Sent);
    }

    [Fact]
    public async Task ADifferentLaterEventOnTheSameTradeStillSends()
    {
        var (service, handler) = NewService();
        await service.RecordAndTrackAsync(new[] { TriggeredResult("ETH/USDT", "1H") });

        await service.RecordAndTrackAsync(new[] { TriggeredResult("ETH/USDT", "1H") with { LivePrice = 111m } }); // TP1
        Assert.Equal(1, handler.Sent);

        await service.RecordAndTrackAsync(new[] { TriggeredResult("ETH/USDT", "1H") with { LivePrice = 121m } }); // TP2 - a different, real, later event
        Assert.Equal(2, handler.Sent);
    }

    [Fact]
    public async Task A15MinuteOutcomeStillNeverSendsEvenWithTheDedupeInPlace()
    {
        // Regression guard: an earlier attempt at this fix accidentally made every terminal
        // status bypass the 15m/weekend Telegram gate. It must not.
        var (service, handler) = NewService();
        await service.RecordAndTrackAsync(new[] { TriggeredResult("XAU/USD", "15m") });

        await service.RecordAndTrackAsync(new[] { TriggeredResult("XAU/USD", "15m") with { LivePrice = 145m } }); // TP3, but 15m

        Assert.Equal(0, handler.Sent);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { /* best effort */ }
    }
}
