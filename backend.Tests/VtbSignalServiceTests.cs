using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Tests;

public class VtbSignalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-vtb-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Today = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc); // Monday

    private sealed class Env(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public List<NormalizedCandle> Candles { get; set; } = new();
        public List<string> TelegramMessages { get; } = new();
        public bool TelegramAccepted { get; set; } = true;
        public List<NormalizedCandle> SpxCandles { get; set; } = new();
        public int MarketRequests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.telegram.org")
            {
                TelegramMessages.Add(await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(TelegramAccepted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(TelegramAccepted ? "{\"ok\":true}" : "{\"ok\":false}")
                };
            }
            if (request.RequestUri.Host == "api.exchange.coinbase.com")
            {
                MarketRequests++;
                var rows = Candles.Select(c => new decimal[]
                {
                    new DateTimeOffset(c.OpenTimeUtc).ToUnixTimeSeconds(), c.Low, c.High, c.Open, c.Close, 1m
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(rows)) };
            }
            if (request.RequestUri.Host == "query1.finance.yahoo.com" && request.RequestUri.AbsoluteUri.Contains("GSPC") && SpxCandles.Count > 0)
            {
                var body = new { chart = new { result = new[] { new {
                    timestamp = SpxCandles.Select(c => new DateTimeOffset(c.OpenTimeUtc.AddHours(14)).ToUnixTimeSeconds()),
                    indicators = new { quote = new[] { new {
                        open = SpxCandles.Select(c => c.Open), high = SpxCandles.Select(c => c.High),
                        low = SpxCandles.Select(c => c.Low), close = SpxCandles.Select(c => c.Close)
                    } } }
                } } } };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"chart\":{\"result\":[]}}") };
        }
    }

    private VtbSignalService Service(Http http, bool configured = true, DateTime? now = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = configured ? "test-token" : null,
            ["Telegram:ChatId"] = "main", ["Telegram:VtbChatId"] = "vtb"
        }).Build();
        return new VtbSignalService(http, config, new Env(_root), NullLogger<VtbSignalService>.Instance,
            () => now ?? Today.AddHours(12));
    }

    private static NormalizedCandle Daily(DateTime day, decimal high = 101m, decimal low = 99m, decimal close = 100m) =>
        new("BTC/USDT", "BTC-USD", "Coinbase", Timeframe.Daily, day, day.AddDays(1),
            100m, high, low, close, null, day < Today, DateTime.UtcNow, DataQuality.Ok);

    private static List<NormalizedCandle> SpxBreakout(DateTime day) => Enumerable.Range(1, 340)
        .Select(i => Daily(day.AddDays(-i))).Where(c => c.OpenTimeUtc.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
        .Reverse().Append(Daily(day, high: 111m, close: 110m)).ToList();

    [Fact]
    public async Task SpxConfirmedEntryPersistsAndAlertsOnceAcrossRestart()
    {
        var http = new Http { SpxCandles = SpxBreakout(Today) };
        var now = Today.AddHours(21).AddMinutes(30);
        var service = Service(http, now: now);
        await service.ProcessAsync();
        var entry = Assert.Single(service.GetAll());
        Assert.Equal(SpxTrendRules.Symbol, entry.Symbol);
        Assert.Equal(SpxTrendRules.ExitMode, entry.ExitMode);
        Assert.Equal(0m, entry.Target);
        Assert.Equal(Today.AddHours(21).AddMinutes(15), entry.ConfirmedAtUtc);
        Assert.Single(http.TelegramMessages);
        Assert.Contains("chat_id=vtb", http.TelegramMessages[0]);
        await Service(http, now: now.AddMinutes(1)).ProcessAsync();
        Assert.Single(http.TelegramMessages);
    }

    [Theory]
    [InlineData(21, 14)] // unfinished bar
    [InlineData(22, 1)] // missed entry window; no stale backfill
    public async Task SpxIncompleteOrStaleEntryDoesNotAlert(int hour, int minute)
    {
        var http = new Http { SpxCandles = SpxBreakout(Today) };
        var service = Service(http, now: Today.AddHours(hour).AddMinutes(minute));
        await service.ProcessAsync();
        Assert.Empty(service.GetAll());
        Assert.Empty(http.TelegramMessages);
    }

    [Fact]
    public async Task SpxFridaySignalDoesNotOpenOnSaturday()
    {
        var friday = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var service = Service(new Http { SpxCandles = SpxBreakout(friday) }, now: friday.AddDays(1).AddHours(12));
        await service.ProcessAsync();
        Assert.Empty(service.GetAll());
    }

    [Fact]
    public async Task SpxStopClosesSavedPositionEvenOnWeekendButDoesNotSendUnannouncedOutcome()
    {
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-signal-log.json"), new[] {
            new VtbSignalService.VtbLogEntry("spx-saved", SpxTrendRules.Symbol, "Long", Today.AddDays(-4),
                100m, 90m, 0m, 10m, "Open", null, null, null, null, 1m, ExitMode: SpxTrendRules.ExitMode)
        });
        var http = new Http { SpxCandles = new() { Daily(Today, low: 89m) } };
        var service = Service(http, now: Today.AddDays(5));
        await service.ProcessAsync();
        var closed = Assert.Single(service.GetAll());
        Assert.Equal("Closed", closed.Status);
        Assert.Equal("Stop", closed.ExitReason);
        Assert.Equal(-1m, closed.R);
        Assert.Empty(http.TelegramMessages);
    }

    private void SeedLegacyOpen(DateTime? entryTimeUtc = null, bool entryDelivered = true)
    {
        // Matches the pre-fix schema, including the absence of notification fields.
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-signal-log.json"), new[]
        {
            new { Id = "saved-short", Symbol = "BTC/USDT", Side = "Short", EntryTimeUtc = entryTimeUtc ?? Today.AddDays(-3),
                EntryRef = 100m, Stop = 110m, Target = 80m, Risk = 10m, Status = "Open", RiskMultiplier = 0.5m }
        });
        var stale = Enumerable.Range(1, 225).Select(i => Daily(Today.AddDays(-i))).Reverse().ToList();
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-history-cache.json"), new Dictionary<string, object>
        {
            ["BTC/USDT"] = new { FetchedAtUtc = Today.AddHours(12), Candles = stale }
        });
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-telegram-delivery.json"), new
        {
            Pending = new Dictionary<string, string>(),
            Receipts = entryDelivered ? new Dictionary<string, TelegramDeliveryReceipt>
            {
                ["saved-short:open"] = new("saved-short:open", "Delivered", Today.AddDays(-3),
                    Attempts: 1, DeliveredAtUtc: Today.AddDays(-3))
            } : new Dictionary<string, TelegramDeliveryReceipt>()
        });
    }

    [Fact]
    public async Task HiddenCancelledGoldIsNotReturnedOrReopened()
    {
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-signal-log.json"), new[] {
            new VtbSignalService.VtbLogEntry("cancelled-gold", "XAU/USD", "Short", Today.AddDays(-2),
                100m, 110m, 80m, 10m, "Cancelled", null, null, null, null, .5m, HiddenFromLedger: true)
        });
        var service = Service(new Http());
        await service.ProcessAsync();
        Assert.Empty(service.GetAll());
        var saved = DiskCache.Load<List<VtbSignalService.VtbLogEntry>>(Path.Combine(_root, ".cache", "vtb-signal-log.json"));
        Assert.Equal("Cancelled", Assert.Single(saved!).Status);
    }

    [Fact]
    public void CancelledReplayEntryDoesNotBlockLaterBreakout()
    {
        var bars = Enumerable.Range(0, 225).Select(i => Daily(Today.AddDays(i - 230))).ToList();
        var cancelledDay = bars[^1].OpenTimeUtc.AddDays(1);
        bars.Add(Daily(cancelledDay, high: 111m, low: 109m, close: 110m) with { Open = 110m });
        bars.Add(Daily(cancelledDay.AddDays(1), high: 112m, low: 109m, close: 111m) with { Open = 110m });
        var freshDay = cancelledDay.AddDays(2);
        bars.Add(Daily(freshDay, high: 121m, low: 119m, close: 120m) with { Open = 120m });
        var original = VtbRules.Replay(bars, 55, 100m);
        Assert.Equal(cancelledDay, original.Open!.EntryTimeUtc);
        var fixedReplay = VtbRules.Replay(bars, 55, 100m, new HashSet<DateTime> { cancelledDay, cancelledDay.AddDays(1) });
        Assert.Equal(freshDay, fixedReplay.Open!.EntryTimeUtc);
        Assert.DoesNotContain(fixedReplay.Closed, t => t.EntryTimeUtc == cancelledDay);
        var unchanged = VtbRules.Replay(bars, 55, 100m, new HashSet<DateTime>());
        Assert.Equal(original.Open, unchanged.Open);
        Assert.Equal(original.Closed, unchanged.Closed);
    }

    [Fact]
    public async Task SaturdayGoldPendingEntryDoesNotAlert()
    {
        var saturday = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-signal-log.json"), new[]
        {
            new VtbSignalService.VtbLogEntry("weekend-gold", "XAU/USD", "Short", saturday.Date.AddDays(-1),
                100m, 110m, 80m, 10m, "Open", null, null, null, null, 0.5m, OpenAlertPending: true)
        });
        var http = new Http();
        await Service(http, now: saturday).ProcessAsync();
        Assert.Empty(http.TelegramMessages);
    }

    [Fact]
    public async Task WarmCacheStillFetchesAndSettlesSavedLevelsOnTheFormingDay()
    {
        SeedLegacyOpen();
        var http = new Http { Candles = new() { Daily(Today, low: 79m) } };
        var service = Service(http);
        await service.ProcessAsync();

        var entry = Assert.Single(service.GetAll());
        Assert.Equal("Closed", entry.Status);
        Assert.Equal("Target", entry.ExitReason);
        Assert.Equal(80m, entry.Exit);
        Assert.Equal(1m, entry.R);
        Assert.True(entry.ExitTimeUtc <= Today.AddHours(12));
        Assert.False(entry.OutcomeAlertPending);
        Assert.True(http.MarketRequests > 0);
        Assert.Contains("chat_id=vtb", Assert.Single(http.TelegramMessages));
        Assert.False(File.Exists(Path.Combine(_root, ".cache", "telegram-delivery.json")));

        await Service(http).ProcessAsync();
        Assert.Single(http.TelegramMessages);
        using var risk = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, ".cache", "vtb-risk-state.json")));
        Assert.Equal(1m, risk.RootElement.GetProperty("CumulativeR").GetDecimal());
    }

    [Fact]
    public async Task FailedOutcomeRetriesAfterRestartWithoutClosingOrCreditingTwice()
    {
        SeedLegacyOpen();
        var http = new Http { Candles = new() { Daily(Today, low: 79m) }, TelegramAccepted = false };
        await Service(http).ProcessAsync();
        Assert.Single(http.TelegramMessages);
        http.TelegramAccepted = true;
        var restarted = Service(http);
        await restarted.ProcessAsync();
        await restarted.ProcessAsync();
        Assert.Equal(2, http.TelegramMessages.Count);
        Assert.Equal(1m, Assert.Single(restarted.GetAll()).R);
        using var delivery = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, ".cache", "vtb-telegram-delivery.json")));
        Assert.Equal("Delivered", delivery.RootElement.GetProperty("Receipts").GetProperty("saved-short:outcome").GetProperty("Status").GetString());
    }

    [Fact]
    public async Task MissingConfigurationKeepsOutcomePendingUntilConfiguredOnRestart()
    {
        SeedLegacyOpen();
        var http = new Http { Candles = new() { Daily(Today, low: 79m) } };
        var service = Service(http, configured: false);
        await service.ProcessAsync();
        Assert.True(Assert.Single(service.GetAll()).OutcomeAlertPending);
        Assert.Empty(http.TelegramMessages);
        await Service(http).ProcessAsync();
        Assert.Single(http.TelegramMessages);
    }

    [Fact]
    public async Task DevelopingDailyBreakoutDoesNotOpenAPosition()
    {
        var http = new Http
        {
            Candles = Enumerable.Range(1, 225).Select(i => Daily(Today.AddDays(-i))).Reverse().ToList()
        };
        http.Candles.Add(Daily(Today, high: 111m, close: 110m));
        var service = Service(http);
        await service.ProcessAsync();
        Assert.Empty(service.GetAll());
        Assert.Empty(http.TelegramMessages);
    }

    [Fact]
    public async Task FailedOpenAlertRetriesWithoutCreatingAnotherPosition()
    {
        var http = new Http
        {
            Candles = Enumerable.Range(2, 225).Select(i => Daily(Today.AddDays(-i))).Reverse().ToList(),
            TelegramAccepted = false
        };
        http.Candles.Add(Daily(Today.AddDays(-1), high: 111m, close: 110m));
        var service = Service(http, now: Today.AddMinutes(5));
        await service.ProcessAsync();
        Assert.NotEmpty(service.GetAll());
        var ids = service.GetAll().Select(e => e.Id).ToArray();
        http.TelegramAccepted = true;
        var restarted = Service(http, now: Today.AddMinutes(10));
        await restarted.ProcessAsync();
        Assert.Equal(ids, restarted.GetAll().Select(e => e.Id).ToArray());
        Assert.All(restarted.GetAll(), e => Assert.Equal("Open", e.Status));
        Assert.All(restarted.GetAll(), e => Assert.Equal(Today, e.ConfirmedAtUtc));
        Assert.NotEmpty(http.TelegramMessages);
    }

    private static Http FreshBreakout() => new() { Candles = Enumerable.Range(2, 225)
        .Select(i => Daily(Today.AddDays(-i))).Reverse()
        .Append(Daily(Today.AddDays(-1), high: 111m, close: 110m)).ToList() };

    [Theory]
    [InlineData(16)]
    [InlineData(720)]
    [InlineData(7200)]
    public async Task HistoricalBreakoutCannotBackfillAfterFreshWindow(int minutes)
    {
        var http = FreshBreakout();
        var service = Service(http, now: Today.AddMinutes(minutes));
        await service.ProcessAsync();
        Assert.Empty(service.GetAll());
        Assert.Empty(http.TelegramMessages);
    }

    [Theory]
    [InlineData("Long")]
    [InlineData("Short")]
    public async Task SavedOpenPositionBlocksEitherDirectionForSamePair(string side)
    {
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-signal-log.json"), new[] {
            new VtbSignalService.VtbLogEntry("existing", "BTC/USDT", side, Today.AddDays(-3),
                100m, side == "Long" ? 50m : 150m, side == "Long" ? 150m : 50m,
                50m, "Open", null, null, null, null, .5m)
        });
        var service = Service(FreshBreakout(), now: Today.AddMinutes(5));
        await service.ProcessAsync();
        Assert.Single(service.GetAll(), e => e.Symbol == "BTC/USDT");
        Assert.Equal("Open", service.GetAll().Single(e => e.Id == "existing").Status);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public async Task ClosedPositionOccupancyUsesConfirmationTime(int exitMinutes, bool admitted)
    {
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-signal-log.json"), new[] {
            new VtbSignalService.VtbLogEntry("previous", "BTC/USDT", "Long", Today.AddDays(-3),
                100m, 50m, 150m, 50m, "Closed", Today.AddMinutes(exitMinutes), 150m, "Target", 1m, 1m)
        });
        var service = Service(FreshBreakout(), now: Today.AddMinutes(5));
        await service.ProcessAsync();
        Assert.Equal(admitted ? 2 : 1, service.GetAll().Count(e => e.Symbol == "BTC/USDT"));
    }

    [Fact]
    public async Task RetryExpiresWithoutAlteringTrackedPosition()
    {
        var http = FreshBreakout();
        http.TelegramAccepted = false;
        await Service(http, now: Today.AddMinutes(5)).ProcessAsync();
        var attempts = http.TelegramMessages.Count;
        Assert.True(attempts > 0);
        http.TelegramAccepted = true;
        var service = Service(http, now: Today.AddMinutes(16));
        await service.ProcessAsync();
        Assert.Equal(attempts, http.TelegramMessages.Count);
        Assert.All(service.GetAll(), e => { Assert.Equal("Open", e.Status); Assert.False(e.OpenAlertPending); });
    }

    [Fact]
    public async Task ClosingSavedPositionDuringScanCannotImportAnEarlierBreakout()
    {
        SeedLegacyOpen(entryTimeUtc: Today.AddDays(-8));
        var http = new Http { Candles = Enumerable.Range(2, 225)
            .Select(i => Daily(Today.AddDays(-i))).Reverse().ToList() };
        http.Candles.Add(Daily(Today.AddDays(-1), high: 101m, low: 89m, close: 90m));
        // Saved short reaches its target now, while the historical replay short stays open.
        http.Candles.Add(Daily(Today, high: 91m, low: 79m, close: 85m));
        var service = Service(http, now: Today.AddMinutes(5));
        await service.ProcessAsync();
        var btc = Assert.Single(service.GetAll(), e => e.Symbol == "BTC/USDT");
        Assert.Equal("saved-short", btc.Id);
        Assert.Equal("Closed", btc.Status);
        Assert.DoesNotContain(http.TelegramMessages, m => m.Contains("Entry+90"));
    }

    [Fact]
    public void TimeExitWaitsForDailyClose()
    {
        var position = new VtbPosition(VtbSide.Long, Today.AddDays(-21), 100m, 10m, 90m, 120m);
        var bar = Daily(Today);
        Assert.Null(VtbRules.Settle(position, new[] { bar }, Today.AddHours(12)));
        var closed = VtbRules.Settle(position, new[] { bar with { IsComplete = true } }, Today.AddDays(1));
        Assert.Equal(VtbExitReason.Time, closed!.Reason);
    }

    [Theory]
    [InlineData("2026-11-14T22:59:59Z", true)]
    [InlineData("2026-11-14T23:00:00Z", false)]
    [InlineData("2026-11-15T22:59:59Z", false)]
    [InlineData("2026-11-15T23:00:00Z", true)]
    public void SundayPauseUsesLagosMidnight(string utc, bool allowed)
    {
        Assert.Equal(allowed, MarketHours.NewEntriesAllowed(DateTimeOffset.Parse(utc).UtcDateTime));
    }

    [Fact]
    public async Task SundayDoesNotOpenAQualifyingBreakout()
    {
        var sunday = Today.AddDays(-1);
        var http = new Http
        {
            Candles = Enumerable.Range(1, 225).Select(i => Daily(sunday.AddDays(-i))).Reverse().ToList()
        };
        http.Candles[^1] = Daily(sunday.AddDays(-1), high: 111m, close: 110m);
        var service = Service(http, now: sunday.AddHours(12));
        await service.ProcessAsync();
        Assert.Empty(service.GetAll());
        Assert.Empty(http.TelegramMessages);
    }

    [Theory]
    [InlineData(101, 79, "Target", 1)]
    [InlineData(111, 99, "Stop", -0.5)]
    public async Task SundayStillClosesExistingTradesAndSendsOutcomes(
        double high, double low, string reason, double realizedR)
    {
        SeedLegacyOpen();
        var sunday = Today.AddDays(-1);
        var http = new Http { Candles = new() { Daily(sunday, high: (decimal)high, low: (decimal)low) } };
        var service = Service(http, now: sunday.AddHours(12));
        await service.ProcessAsync();
        var entry = Assert.Single(service.GetAll());
        Assert.Equal("Closed", entry.Status);
        Assert.Equal(reason, entry.ExitReason);
        Assert.Equal((decimal)realizedR, entry.R);
        Assert.Single(http.TelegramMessages);
    }

    [Fact]
    public async Task SundayStillAppliesTimeExits()
    {
        SeedLegacyOpen(Today.AddDays(-22));
        var sunday = Today.AddDays(-1);
        var http = new Http { Candles = new() { Daily(sunday.AddDays(-1)) } };
        var service = Service(http, now: sunday.AddHours(12));
        await service.ProcessAsync();
        var entry = Assert.Single(service.GetAll());
        Assert.Equal("Closed", entry.Status);
        Assert.Equal("Time", entry.ExitReason);
        Assert.Single(http.TelegramMessages);
    }

    private void SeedQueuedAlerts()
    {
        var created = Today.AddDays(-2);
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-telegram-delivery.json"), new
        {
            Pending = new Dictionary<string, string>
            {
                ["saved-short:open"] = "Pending VTB entry",
                ["previous-trade:outcome"] = "Pending VTB outcome"
            },
            Receipts = new Dictionary<string, TelegramDeliveryReceipt>
            {
                ["saved-short:open"] = new("saved-short:open", "Retrying", created, Attempts: 1),
                ["previous-trade:outcome"] = new("previous-trade:outcome", "Queued", created),
                ["previous-trade:open"] = new("previous-trade:open", "Delivered", created,
                    Attempts: 1, DeliveredAtUtc: created)
            }
        });
    }

    [Fact]
    public async Task SundayDeliversOutcomesButStaleQueuedEntryNeverResumesOnMonday()
    {
        SeedLegacyOpen();
        SeedQueuedAlerts();
        var http = new Http { Candles = new() { Daily(Today.AddDays(-1)) } };
        await Service(http, now: Today.AddDays(-1).AddHours(12)).ProcessAsync();
        Assert.Contains("Pending+VTB+outcome", Assert.Single(http.TelegramMessages));

        await Service(http).ProcessAsync();
        Assert.Single(http.TelegramMessages);
        using var delivery = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, ".cache", "vtb-telegram-delivery.json")));
        Assert.Equal("Cancelled", delivery.RootElement.GetProperty("Receipts").GetProperty("saved-short:open").GetProperty("Status").GetString());
    }

    [Fact]
    public async Task AnEntryAlertHeldOnSundayIsCancelledWhenItsPositionCloses()
    {
        SeedLegacyOpen();
        SeedQueuedAlerts();
        var sunday = Today.AddDays(-1);
        var http = new Http { Candles = new() { Daily(sunday, low: 79m) } };
        var service = Service(http, now: sunday.AddHours(12));
        await service.ProcessAsync();
        await Service(http).ProcessAsync();
        Assert.All(http.TelegramMessages, message => Assert.DoesNotContain("Pending+VTB+entry", message));
        using var delivery = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, ".cache", "vtb-telegram-delivery.json")));
        Assert.Equal("Cancelled", delivery.RootElement.GetProperty("Receipts").GetProperty("saved-short:open")
            .GetProperty("Status").GetString());
    }

    [Theory]
    [InlineData(101, 79)]
    [InlineData(111, 99)]
    public async Task VtbWithoutDeliveredEntryTracksClosureButSendsNoOutcome(double high, double low)
    {
        SeedLegacyOpen(entryDelivered: false);
        var http = new Http { Candles = new() { Daily(Today, high: (decimal)high, low: (decimal)low) } };
        var service = Service(http);
        await service.ProcessAsync();
        Assert.Equal("Closed", Assert.Single(service.GetAll()).Status);
        Assert.Empty(http.TelegramMessages);
        await Service(http).ProcessAsync();
        Assert.Empty(http.TelegramMessages);
    }

    [Fact]
    public async Task RetryingVtbEntryDoesNotAuthorizeItsOutcome()
    {
        SeedLegacyOpen(entryDelivered: false);
        SeedQueuedAlerts();
        var http = new Http { Candles = new() { Daily(Today, low: 79m) } };
        var service = Service(http);
        await service.ProcessAsync();
        Assert.Equal("Closed", Assert.Single(service.GetAll()).Status);
        // The only delivered message belongs to the older, actually-announced trade.
        Assert.Contains("Pending+VTB+outcome", Assert.Single(http.TelegramMessages));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, ".cache", "vtb-telegram-delivery.json")));
        Assert.False(state.RootElement.GetProperty("Receipts").TryGetProperty("saved-short:outcome", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
