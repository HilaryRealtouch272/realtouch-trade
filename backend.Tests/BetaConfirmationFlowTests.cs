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

    private BetaConfirmationFlow Flow(string? chat = "beta-group", IFineCandleSource? fineSource = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "test-token",
            ["Telegram:ChatId"] = "real-group",
            ["Telegram:BetaChatId"] = chat
        }).Build();
        return new BetaConfirmationFlow(_requests, config, new Env(_root), NullLoggerFactory.Instance, () => _now, fineSource);
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
    public void BetaDoesNotIntroduceASeparateExpiryGateForAnOtherwiseQualifiedRealPlan()
    {
        var fine = FineCandles();
        var source = Setup("Daily", fine).Signal! with { ExpiryUtc = _now.AddMinutes(-1) };
        Assert.True(BetaEntryConfirmation.Confirm(source, _now.AddHours(-12), fine, _now).Triggered);
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

    [Fact]
    public async Task RealAndBetaKeepSeparateGroupsPositionsCooldownsAndDeliveryState()
    {
        var fine = FineCandles();
        var source = Setup("Daily", fine);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "test-token", ["Telegram:ChatId"] = "real-group"
        }).Build();
        var env = new Env(_root);
        var delivery = new TelegramDeliveryOutbox(_requests, config, env, NullLogger.Instance);
        var notifier = new RealtouchSmartTrade.Api.Providers.TelegramNotifier(delivery, config);
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance, delivery);
        var real = source with { Signal = source.Signal! with { Triggered = true } };
        await alerts.CheckAndNotifyAsync(new[] { real });
        await ledger.RecordAndTrackAsync(new[] { real, Prices(fine) });
        await delivery.FlushAsync();
        var realLedger = JsonSerializer.Serialize(ledger.GetAll());
        var files = Directory.GetFiles(Path.Combine(_root, ".cache")).ToDictionary(p => p, File.ReadAllText);

        _requests.FailuresRemaining = 1;
        var beta = Flow();
        await beta.ProcessAsync(new[] { source, Prices(fine) });

        Assert.Equal(realLedger, JsonSerializer.Serialize(ledger.GetAll()));
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllText(file.Key));
        var mainEntry = Assert.Single(ledger.GetAll());
        var betaEntry = Assert.Single(beta.GetAll());
        Assert.Equal(mainEntry.SourceSetupId, betaEntry.SourceSetupId);
        Assert.Equal(mainEntry.Stop, betaEntry.Stop);
        Assert.Equal(mainEntry.Tp1, betaEntry.Tp1);
        Assert.Equal(mainEntry.Tp2, betaEntry.Tp2);
        Assert.Equal(mainEntry.Tp3, betaEntry.Tp3);
        Assert.Equal(mainEntry.RiskAmount, betaEntry.RiskAmount);
        Assert.Equal(mainEntry.RiskPercent, betaEntry.RiskPercent);
        Assert.Equal(mainEntry.PositionSize, betaEntry.PositionSize);
        Assert.Equal("Daily", mainEntry.EntryTimeframe);
        Assert.Equal("15m", betaEntry.EntryTimeframe);
        Assert.NotNull(betaEntry.ConfirmationCandleUtc);
        Assert.Equal("Delivered", Assert.Single(alerts.GetStatuses()).DeliveryState);
        Assert.Equal("Retrying", Assert.Single(beta.GetStatuses()).DeliveryState);
        Assert.Equal(new[] { "real-group", "beta-group" }, _requests.Messages.Select(m => m["chat_id"]));
    }

    private sealed class FineSource(IReadOnlyList<NormalizedCandle> candles) : IFineCandleSource
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<NormalizedCandle>> GetOneMinuteAsync(string symbol, DateTime sinceUtc)
        {
            Calls++;
            return Task.FromResult(candles);
        }
    }

    [Fact]
    public async Task BetaUsesTheSameFineCandleOutcomeTrackingAsReal()
    {
        var fine = FineCandles();
        var entry = SignalLogService.CreateEntry(Setup("Daily", fine), _now.AddMinutes(-40));
        DiskCache.Save(Path.Combine(_root, ".cache", "beta-15m", ".cache", "signal-log.json"), new[] { entry });
        var minute = fine[^1] with
        {
            OpenTimeUtc = _now.AddMinutes(-3), CloseTimeUtc = _now.AddMinutes(-2),
            Open = entry.Entry, Low = entry.Entry - 0.1m, High = entry.Tp1 + 0.1m, Close = entry.Tp1
        };
        fine[^1] = fine[^1] with { Low = entry.Stop - 1m };
        var fineSource = new FineSource(new[] { minute });
        var beta = Flow(fineSource: fineSource);
        await beta.ProcessAsync(new[] { Prices(fine) });

        Assert.Equal(1, fineSource.Calls);
        Assert.Equal("Tp1Hit", Assert.Single(beta.GetAll()).Status);
    }

    [Fact]
    public async Task RejectedHigherTimeframeSetupsCannotRemainEligibleInBeta()
    {
        var fine = FineCandles();
        var source = Setup("Daily", fine);
        var beta = Flow();
        await beta.ProcessAsync(new[] { source });
        await beta.ProcessAsync(new[] { source with { Success = false, Signal = null, Reason = "Hard calendar veto" }, Prices(fine) });

        Assert.Empty(beta.GetAll());
        Assert.Empty(_requests.Messages);
        Assert.Contains("Hard calendar veto", Assert.Single(beta.GetStatuses()).Reason);
    }

    [Fact]
    public void ComparisonsMatchThePlanAndKeepUnresolvedReturnsSeparate()
    {
        var fine = FineCandles();
        var source = Setup("Daily", fine);
        var real = SignalLogService.CreateEntry(source, _now) with
        {
            Status = "StoppedOut", ClosedAtUtc = _now, RealizedR = 1m, NetRealizedR = 0.8m
        };
        var betaSignal = BetaEntryConfirmation.Confirm(source.Signal!, _now.AddHours(-12), fine, _now);
        var beta = SignalLogService.CreateEntry(source with { Signal = betaSignal }, _now);
        using var report = JsonDocument.Parse(JsonSerializer.Serialize(
            SignalComparison.Build(new[] { real, real with { SourceSetupId = null } }, new[] { beta })));
        var row = Assert.Single(report.RootElement.EnumerateArray());
        Assert.Equal(0.8m, row.GetProperty("real").GetProperty("netR").GetDecimal());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("beta").GetProperty("netR").ValueKind);
        Assert.Equal(1, row.GetProperty("real").GetProperty("trades").GetInt32());
        Assert.Equal(real.SourceSetupId, beta.SourceSetupId);
        using var missingCosts = JsonDocument.Parse(JsonSerializer.Serialize(
            SignalComparison.Build(new[] { real with { NetRealizedR = null } }, Array.Empty<QualificationLogEntry>())));
        Assert.Equal(JsonValueKind.Null,
            missingCosts.RootElement[0].GetProperty("real").GetProperty("netR").ValueKind);
    }

    [Fact]
    public async Task ANewCalendarVetoBlocksEntryEvenWhenTheCachedSetupWasPreviouslyQualified()
    {
        var fine = FineCandles();
        DiskCache.Save(Path.Combine(_root, ".cache", "calendar-cache.json"), new
        {
            Result = new CalendarResult(true, null, new[]
            {
                new EconomicEvent("CPI", "USD", "high", _now, null, null, null, "test", _now)
            }),
            FetchedAtUtc = _now
        });
        var beta = Flow();
        await beta.ProcessAsync(new[] { Setup("Daily", fine), Prices(fine) });
        Assert.Empty(beta.GetAll());
        Assert.Empty(_requests.Messages);
        Assert.Contains("Hard economic-calendar veto", Assert.Single(beta.GetStatuses()).Reason);
    }

    public void Dispose()
    {
        _requests.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
