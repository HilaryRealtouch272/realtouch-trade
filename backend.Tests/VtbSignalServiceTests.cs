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
    private static readonly DateTime Today = DateTime.UtcNow.Date;

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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"chart\":{\"result\":[]}}") };
        }
    }

    private VtbSignalService Service(Http http, bool configured = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = configured ? "test-token" : null,
            ["Telegram:ChatId"] = "main", ["Telegram:VtbChatId"] = "vtb"
        }).Build();
        return new VtbSignalService(http, config, new Env(_root), NullLogger<VtbSignalService>.Instance);
    }

    private static NormalizedCandle Daily(DateTime day, decimal high = 101m, decimal low = 99m, decimal close = 100m) =>
        new("BTC/USDT", "BTC-USD", "Coinbase", Timeframe.Daily, day, day.AddDays(1),
            100m, high, low, close, null, day < Today, DateTime.UtcNow, DataQuality.Ok);

    private void SeedLegacyOpen()
    {
        // Matches the pre-fix schema, including the absence of notification fields.
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-signal-log.json"), new[]
        {
            new { Id = "saved-short", Symbol = "BTC/USDT", Side = "Short", EntryTimeUtc = Today.AddDays(-3),
                EntryRef = 100m, Stop = 110m, Target = 80m, Risk = 10m, Status = "Open", RiskMultiplier = 0.5m }
        });
        var stale = Enumerable.Range(1, 225).Select(i => Daily(Today.AddDays(-i))).Reverse().ToList();
        DiskCache.Save(Path.Combine(_root, ".cache", "vtb-history-cache.json"), new Dictionary<string, object>
        {
            ["BTC/USDT"] = new { FetchedAtUtc = DateTime.UtcNow, Candles = stale }
        });
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
        Assert.True(entry.ExitTimeUtc <= DateTime.UtcNow);
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
        var service = Service(http);
        await service.ProcessAsync();
        Assert.NotEmpty(service.GetAll());
        var ids = service.GetAll().Select(e => e.Id).ToArray();
        http.TelegramAccepted = true;
        var restarted = Service(http);
        await restarted.ProcessAsync();
        Assert.Equal(ids, restarted.GetAll().Select(e => e.Id).ToArray());
        Assert.All(restarted.GetAll(), e => Assert.Equal("Open", e.Status));
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
