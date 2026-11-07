using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class BetaConfirmationFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-beta-15m-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;
    private readonly Requests _requests = new();

    private sealed class Env(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Requests : HttpMessageHandler, IHttpClientFactory
    {
        public List<Dictionary<string, string>> Messages { get; } = new();
        public int FailuresRemaining { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var fields = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            Messages.Add(fields.ToDictionary(p => p.Key, p => p.Value.ToString()));
            var failed = FailuresRemaining-- > 0;
            return new HttpResponseMessage(failed ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(failed ? "{}" : "{\"ok\":true}")
            };
        }
    }

    private BetaConfirmationFlow Flow(string? chat = "beta-group")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "test-token",
            ["Telegram:ChatId"] = "real-group",
            ["Telegram:BetaChatId"] = chat
        }).Build();
        return new BetaConfirmationFlow(_requests, config, new Env(_root), NullLoggerFactory.Instance, () => _now);
    }

    private List<NormalizedCandle> FineCandles() =>
        CandleFixtures.FromOhlc(CandleFixtures.UptrendBars(96), Timeframe.M15, _now.AddMinutes(-1441))
            .Select(c => c with { CanonicalSymbol = "BTC/USDT" }).ToList();

    private OrchestratorResult Setup(string timeframe, List<NormalizedCandle> fine)
    {
        var price = fine[^1].Close;
        var signal = new SignalResult("real-id", "real-version", "BTC/USDT", "crypto", "Coinbase",
            timeframe, timeframe, SetupDirection.Long, MarketCondition.TrendingBullish,
            SetupModelType.TrendContinuationPullback, SignalState.Watchlist, 80, "B",
            _now, _now.AddDays(2), price + 20m, price - 0.5m, price + 0.5m, price, false,
            price - 5m, price + 5m, price + 10m, price + 15m, 2m, 0.5m, 50m, 10m,
            Array.Empty<KeyLevel>(), Array.Empty<ConfluenceFamilyScore>(), "Qualified setup",
            "Unchecked", "NoVeto", "", "", "", "Structural invalidation");
        var tf = TimeframeIntervals.ParseLabel(timeframe)!.Value;
        var candle = fine[^1] with
        {
            Timeframe = tf, CloseTimeUtc = _now.AddHours(-12),
            OpenTimeUtc = _now.AddHours(-12) - TimeframeConfig.Duration(tf), Close = price + 20m
        };
        return new OrchestratorResult(true, signal, null, "BTC/USDT", timeframe, Candles: new[] { candle });
    }

    private static OrchestratorResult Prices(List<NormalizedCandle> fine) =>
        new(false, null, "No native 15m setup", "BTC/USDT", "15m", LivePrice: fine[^1].Close, Candles: fine);

    [Theory]
    [InlineData("Weekly")]
    [InlineData("Daily")]
    [InlineData("4H")]
    [InlineData("1H")]
    public async Task BetaConfirmsEachHigherTimeframeWithoutChangingRealInputsOrState(string timeframe)
    {
        var fine = FineCandles();
        var source = Setup(timeframe, fine);
        var original = JsonSerializer.Serialize(source);
        Directory.CreateDirectory(Path.Combine(_root, ".cache"));
        var realPath = Path.Combine(_root, ".cache", "signal-log.json");
        File.WriteAllText(realPath, "real ledger sentinel");
        var flow = Flow();

        await flow.ProcessAsync(new[] { source, Prices(fine) });
        await flow.ProcessAsync(new[] { source, Prices(fine) });

        var entry = Assert.Single(flow.GetAll());
        Assert.Equal(timeframe, entry.Timeframe);
        Assert.Equal(source.Signal!.PreferredEntry, entry.Entry);
        Assert.StartsWith("beta-15m/", entry.StrategyVersion);
        Assert.Equal(original, JsonSerializer.Serialize(source));
        Assert.False(source.Signal.Triggered);
        Assert.Equal("real ledger sentinel", File.ReadAllText(realPath));
        var sent = Assert.Single(_requests.Messages);
        Assert.Equal("beta-group", sent["chat_id"]);
        Assert.StartsWith("🧪 *BETA · 15m confirmation*", sent["text"]);
        Assert.Contains(timeframe, sent["text"]);
    }

    [Fact]
    public async Task RestartPreservesPendingSetupsTradesAndFailedNotificationRetries()
    {
        var fine = FineCandles();
        _requests.FailuresRemaining = 1;
        await Flow().ProcessAsync(new[] { Setup("Daily", fine), Prices(fine) });
        Assert.Single(_requests.Messages);

        var restarted = Flow();
        // Only a new 15m tick is needed; the higher-timeframe setup survives the restart.
        await restarted.ProcessAsync(new[] { Prices(fine) });
        await restarted.ProcessAsync(new[] { Prices(fine) });

        Assert.Single(restarted.GetAll());
        Assert.Equal(2, _requests.Messages.Count);
        Assert.All(_requests.Messages, m => Assert.Equal("beta-group", m["chat_id"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("real-group")]
    public async Task MissingOrSharedBetaDestinationNeverFallsBackToTheRealGroup(string? chat)
    {
        var fine = FineCandles();
        var flow = Flow(chat);
        await flow.ProcessAsync(new[] { Setup("Daily", fine), Prices(fine) });

        Assert.Single(flow.GetAll());
        Assert.Empty(_requests.Messages);
    }

    [Fact]
    public async Task NativeFifteenMinuteSignalsAreNeverAddedToTheHigherTimeframeBetaLedger()
    {
        var fine = FineCandles();
        var source = Setup("15m", fine) with { Candles = fine };
        var flow = Flow();
        await flow.ProcessAsync(new[] { source });
        Assert.Empty(flow.GetAll());
        Assert.Empty(_requests.Messages);
    }

    [Fact]
    public void OldMainTriggerDoesNotBypassMissingStaleOrPrematureBetaConfirmation()
    {
        var fine = FineCandles();
        var source = Setup("Daily", fine).Signal! with { Triggered = true };
        Assert.False(BetaEntryConfirmation.Confirm(source, _now.AddHours(-12), Array.Empty<NormalizedCandle>(), _now).Triggered);
        Assert.False(BetaEntryConfirmation.Confirm(source, _now.AddHours(-12), fine, _now.AddHours(2)).Triggered);
        Assert.False(BetaEntryConfirmation.Confirm(source, _now, fine, _now).Triggered);
        Assert.True(source.Triggered);
    }

    [Fact]
    public async Task OutcomeUpdatesUseOnlyTheBetaLedgerAndRetryAfterARestart()
    {
        var fine = FineCandles();
        var setup = Setup("Daily", fine);
        var entry = SignalLogService.CreateEntry(setup, _now.AddMinutes(-40));
        var betaPath = Path.Combine(_root, ".cache", "beta-15m", ".cache", "signal-log.json");
        DiskCache.Save(betaPath, new[] { entry });
        fine[^1] = fine[^1] with { Low = entry.Stop - 1m };
        _requests.FailuresRemaining = 1;

        var flow = Flow();
        await flow.ProcessAsync(new[] { Prices(fine) });
        Assert.Equal("StoppedOut", Assert.Single(flow.GetAll()).Status);
        var restarted = Flow();
        await restarted.ProcessAsync(new[] { Prices(fine) });

        Assert.Equal(2, _requests.Messages.Count);
        Assert.All(_requests.Messages, m =>
        {
            Assert.Equal("beta-group", m["chat_id"]);
            Assert.StartsWith("🧪 *BETA · 15m confirmation*", m["text"]);
            Assert.Contains("Stopped", m["text"]);
        });
    }

    public void Dispose()
    {
        _requests.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
