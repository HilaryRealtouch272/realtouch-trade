using System.Net;
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

public class SignalDeliveryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-delivery-" + Guid.NewGuid().ToString("N"));
    private sealed class Env(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "{\"ok\":true}";
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }

    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Telegram:BotToken"] = "test-token", ["Telegram:ChatId"] = "main"
    }).Build();

    [Fact]
    public async Task EntryAndOutcomeMessagesRemainQueuedUntilTelegramAcceptsThem()
    {
        var http = new Http { Status = HttpStatusCode.ServiceUnavailable, Body = "{\"ok\":false}" };
        var config = Config();
        var env = new Env(_root);
        var queue = new TelegramDeliveryOutbox(http, config, env, NullLogger.Instance);
        var notifier = new TelegramNotifier(queue, config);
        Assert.True((await notifier.SendAsync("Entry", notificationId: "entry-1")).Success);
        Assert.Equal(0, http.Calls);
        Assert.Equal("Queued", queue.Get("entry-1")!.Status);
        await queue.FlushAsync();
        Assert.Equal("Retrying", queue.Get("entry-1")!.Status);

        http.Status = HttpStatusCode.OK;
        http.Body = "{\"ok\":true}";
        var restarted = new TelegramDeliveryOutbox(http, config, env, NullLogger.Instance);
        await restarted.FlushAsync();
        Assert.Equal("Delivered", restarted.Get("entry-1")!.Status);
        Assert.NotNull(restarted.Get("entry-1")!.DeliveredAtUtc);
        await new TelegramNotifier(restarted, config).SendAsync("Entry", notificationId: "entry-1");
        await restarted.FlushAsync();
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task SuccessfulHttpStatusWithTelegramErrorIsNotAConfirmedDelivery()
    {
        var http = new Http { Body = "{\"ok\":false}" };
        var config = Config();
        var queue = new TelegramDeliveryOutbox(http, config, new Env(_root), NullLogger.Instance);
        await new TelegramNotifier(queue, config).SendAsync("Entry", notificationId: "entry-1");
        await queue.FlushAsync();
        Assert.Equal("Retrying", queue.Get("entry-1")!.Status);
        Assert.Null(queue.Get("entry-1")!.DeliveredAtUtc);
    }

    [Fact]
    public async Task ExistingBetaOutboxMigratesOnceWithoutDroppingOrReplayingItsMessages()
    {
        var http = new Http();
        var config = Config();
        var env = new Env(_root);
        DiskCache.Save(Path.Combine(_root, ".cache", "notification-outbox.json"), new Dictionary<string, string>
        {
            ["old-event"] = "Previously queued beta outcome"
        });
        var queue = new TelegramDeliveryOutbox(http, config, env, NullLogger.Instance, beta: true);
        await queue.FlushAsync();
        await new TelegramDeliveryOutbox(http, config, env, NullLogger.Instance, beta: true).FlushAsync();
        Assert.Equal(1, http.Calls);
        Assert.Equal("Delivered", queue.Get("old-event")!.Status);
    }

    private static OrchestratorResult Triggered()
    {
        var now = Now;
        var signal = new SignalResult("id", "v1", "BTC/USDT", "crypto", "Coinbase", "1H", "1H",
            SetupDirection.Long, MarketCondition.TrendingBullish, SetupModelType.TrendContinuationPullback,
            SignalState.Triggered, 80, "B", now, now.AddDays(2), 100m, 99m, 101m, 100m, true,
            95m, 105m, 110m, 115m, 2m, 0.5m, 50m, 10m, Array.Empty<KeyLevel>(),
            Array.Empty<ConfluenceFamilyScore>(), "", "Unchecked", "NoVeto", "", "", "", "Stop at 95");
        return new(true, signal, null, "BTC/USDT", "1H");
    }

    [Fact]
    public async Task AFailedAcceptanceRetriesTheSameQualificationInsteadOfSilentlyDeduplicatingIt()
    {
        var http = new Http();
        var config = Config();
        config["Telegram:ChatId"] = "";
        var env = new Env(_root);
        var notifier = new TelegramNotifier(http, config);
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance, clock: () => Now);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance, clock: () => Now);
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        Assert.Equal("Delivery failed", Assert.Single(alerts.GetStatuses()).AlertState);
        config["Telegram:ChatId"] = "main";
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        Assert.Equal(1, http.Calls);
    }

    private static OrchestratorResult Triggered(string grade, SetupModelType model = SetupModelType.TrendContinuationPullback, string timeframe = "1H")
    {
        var now = Now;
        var signal = new SignalResult("id", "v1", "BTC/USDT", "crypto", "Coinbase", timeframe, timeframe,
            SetupDirection.Long, MarketCondition.TrendingBullish, model,
            SignalState.Triggered, 80, grade, now, now.AddDays(2), 100m, 99m, 101m, 100m, true,
            95m, 105m, 110m, 115m, 2m, 0.5m, 50m, 10m, Array.Empty<KeyLevel>(),
            Array.Empty<ConfluenceFamilyScore>(), "", "Unchecked", "NoVeto", "", "", "", "Stop at 95");
        return new(true, signal, null, "BTC/USDT", timeframe);
    }

    [Fact]
    public async Task NoFurtherAlertGoesOutForAnAlreadyOpenTradeEvenWithADifferentGradeOrModel()
    {
        // Owner decision: once a trade is open for a symbol+timeframe, Telegram stays silent for
        // it entirely until it closes - no re-alert just because a later scan's same-direction
        // re-evaluation comes back with a different grade or model than what's already open.
        var http = new Http();
        var config = Config();
        var env = new Env(_root);
        var notifier = new TelegramNotifier(http, config);
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance, clock: () => Now);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance, clock: () => Now);

        await alerts.CheckAndNotifyAsync(new[] { Triggered("B") });
        await ledger.RecordAndTrackAsync(new[] { Triggered("B") });
        Assert.Equal(1, http.Calls);

        await alerts.CheckAndNotifyAsync(new[] { Triggered("A+", SetupModelType.BreakoutAndRetest) });

        Assert.Equal(1, http.Calls); // no second Telegram call
        Assert.Equal("Open trade", Assert.Single(alerts.GetStatuses()).AlertState);
    }

    [Fact]
    public async Task AnUnblockedGradeStillGetsItsFirstRealAlertEvenWhenABlockedGradeAlreadyOpenedTheSamePositionOnTheLedger()
    {
        // Owner decision: dedup only applies once a position is open on BOTH the ledger AND
        // Telegram - not the ledger alone. BTC/USDT's 15m carve-out withholds grade A+ from
        // Telegram (dashboard-only) but that grade still opens on the ledger as normal; a later
        // grade B for the same still-open position IS allowed on 15m and must still get its
        // first real Telegram announcement, not be silently swallowed as "already open".
        var http = new Http();
        var config = Config();
        var env = new Env(_root);
        var notifier = new TelegramNotifier(http, config);
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance, clock: () => Now);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance, clock: () => Now);

        await alerts.CheckAndNotifyAsync(new[] { Triggered("A+", timeframe: "15m") });
        Assert.Equal("Blocked", Assert.Single(alerts.GetStatuses()).AlertState);
        await ledger.RecordAndTrackAsync(new[] { Triggered("A+", timeframe: "15m") });
        Assert.Equal(0, http.Calls);

        await alerts.CheckAndNotifyAsync(new[] { Triggered("B", timeframe: "15m") });

        Assert.Equal(1, http.Calls); // the first real alert for this still-open position
    }

    [Fact]
    public async Task LegacySuccessfulAlertStateStillSuppressesDuplicateMessages()
    {
        var http = new Http();
        var config = Config();
        var env = new Env(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".cache"));
        File.WriteAllText(Path.Combine(_root, ".cache", "alert-state.json"),
            "{\"BTC/USDT|1H\":{\"Direction\":\"Long\",\"SetupModel\":\"TrendContinuationPullback\",\"Grade\":\"B\"}}");
        var notifier = new TelegramNotifier(http, config);
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance, clock: () => Now);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance, clock: () => Now);
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        Assert.Equal(0, http.Calls);
        Assert.Equal("Already announced", Assert.Single(alerts.GetStatuses()).AlertState);
    }

    [Fact]
    public async Task SundayBlocksNewLedgerEntriesAndAlertsThenResumesOnMonday()
    {
        var http = new Http();
        var env = new Env(_root);
        var notifier = new TelegramNotifier(http, Config());
        var now = Now.AddDays(-1);
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance, clock: () => now);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance, clock: () => now);

        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        await ledger.RecordAndTrackAsync(new[] { Triggered() });
        Assert.Equal("Blocked", Assert.Single(alerts.GetStatuses()).AlertState);
        Assert.Contains("Sundays", Assert.Single(alerts.GetStatuses()).Reason);
        Assert.Empty(ledger.GetAll());
        Assert.Equal(0, http.Calls);

        now = Now;
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        await ledger.RecordAndTrackAsync(new[] { Triggered() });
        Assert.Single(ledger.GetAll());
        Assert.Equal(1, http.Calls);
    }

    [Theory]
    [InlineData(90, false, "StoppedOut")]
    [InlineData(120, false, "Tp3Hit")]
    [InlineData(100, true, "Expired")]
    public async Task SundayStillTracksClosesAndAlertsExistingMainTrades(int price, bool expired, string status)
    {
        var now = Now.AddDays(-1);
        var saved = SignalLogService.CreateEntry(Triggered(), now.AddHours(-1)) with
        {
            TrackingExpiryUtc = expired ? now.AddMinutes(-1) : now.AddHours(29)
        };
        DiskCache.Save(Path.Combine(_root, ".cache", "signal-log.json"), new[] { saved });
        var http = new Http();
        var ledger = new SignalLogService(new TelegramNotifier(http, Config()), new Env(_root),
            NullLogger<SignalLogService>.Instance, clock: () => now);

        await ledger.RecordAndTrackAsync(new[] { Triggered() with { LivePrice = price } });

        var closed = Assert.Single(ledger.GetAll());
        Assert.Equal(status, closed.Status);
        Assert.NotNull(closed.ClosedAtUtc);
        Assert.Null(ledger.GetOpenDirection("BTC/USDT", "1H"));
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task SundayQueueHoldsEntryMessagesAndSendsOutcomes()
    {
        var now = Now.AddDays(-1);
        var http = new Http();
        var config = Config();
        var queue = new TelegramDeliveryOutbox(http, config, new Env(_root), NullLogger.Instance,
            canDeliver: id => id.StartsWith("outcome:", StringComparison.Ordinal) || MarketHours.NewEntriesAllowed(now));
        var notifier = new TelegramNotifier(queue, config);
        await notifier.SendAsync("Entry", notificationId: "entry-1");
        await notifier.SendAsync("Outcome", notificationId: "outcome:trade-1:closed");
        await queue.FlushAsync();
        Assert.Equal(1, http.Calls);
        Assert.Equal("Queued", queue.Get("entry-1")!.Status);
        Assert.Equal("Delivered", queue.Get("outcome:trade-1:closed")!.Status);

        now = Now;
        await queue.FlushAsync();
        Assert.Equal(2, http.Calls);
        Assert.Equal("Delivered", queue.Get("entry-1")!.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
