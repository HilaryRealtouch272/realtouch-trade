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
        var now = DateTime.UtcNow;
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
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance);
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        Assert.Equal("Delivery failed", Assert.Single(alerts.GetStatuses()).AlertState);
        config["Telegram:ChatId"] = "main";
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        Assert.Equal(1, http.Calls);
    }

    private static OrchestratorResult Triggered(string grade, SetupModelType model = SetupModelType.TrendContinuationPullback)
    {
        var now = DateTime.UtcNow;
        var signal = new SignalResult("id", "v1", "BTC/USDT", "crypto", "Coinbase", "1H", "1H",
            SetupDirection.Long, MarketCondition.TrendingBullish, model,
            SignalState.Triggered, 80, grade, now, now.AddDays(2), 100m, 99m, 101m, 100m, true,
            95m, 105m, 110m, 115m, 2m, 0.5m, 50m, 10m, Array.Empty<KeyLevel>(),
            Array.Empty<ConfluenceFamilyScore>(), "", "Unchecked", "NoVeto", "", "", "", "Stop at 95");
        return new(true, signal, null, "BTC/USDT", "1H");
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
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance);

        await alerts.CheckAndNotifyAsync(new[] { Triggered("B") });
        await ledger.RecordAndTrackAsync(new[] { Triggered("B") });
        Assert.Equal(1, http.Calls);

        await alerts.CheckAndNotifyAsync(new[] { Triggered("A+", SetupModelType.BreakoutAndRetest) });

        Assert.Equal(1, http.Calls); // no second Telegram call
        Assert.Equal("Open trade", Assert.Single(alerts.GetStatuses()).AlertState);
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
        var ledger = new SignalLogService(notifier, env, NullLogger<SignalLogService>.Instance);
        var alerts = new SignalAlertService(notifier, ledger, env, NullLogger<SignalAlertService>.Instance);
        await alerts.CheckAndNotifyAsync(new[] { Triggered() });
        Assert.Equal(0, http.Calls);
        Assert.Equal("Already announced", Assert.Single(alerts.GetStatuses()).AlertState);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
