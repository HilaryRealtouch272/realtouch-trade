using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RealtouchSmartTrade.Api.Services;

namespace RealtouchSmartTrade.Tests;

public class AutoTradingSignalSinkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rst-execution-" + Guid.NewGuid().ToString("N"));
    private sealed class Env(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private AutoTradingSignalSink Sink(bool enabled) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AutoTrading:Enabled"] = enabled.ToString(), ["AutoTrading:FeedSigningKey"] = new string('x', 48) }).Build(),
        NullLogger<AutoTradingSignalSink>.Instance, new Env(root));
    private static VtbSignalService.VtbLogEntry Entry() => new("id", "BTC/USDT", "Short",
        new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc), 100, 110, 80, 10,
        "Open", null, null, null, null, .5m);

    [Fact]
    public void SpxLedgerSignalsCannotReachFixedTargetBrokerWorker()
    {
        Sink(true).Vtb(Entry() with { Symbol = "SPX/USD", ExitMode = "SpxTrend", Target = 0m });
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void DisabledSinkCreatesNoExecutionFiles()
    {
        Sink(false).Vtb(Entry());
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void VtbUsesConfirmedCloseAndNeverRefreshesDeadlineOnRetry()
    {
        Sink(true).Vtb(Entry());
        Sink(true).Vtb(Entry() with { EntryRef = 999 });
        var candidates = DiskCache.Load<Dictionary<string, AutoTradingSignalSink.Candidate>>(
            Path.Combine(root, ".cache", "auto-trading-candidates.json"))!;
        var intent = Assert.Single(candidates).Value.Signal;
        Assert.Equal("vtb:id", intent.Id);
        Assert.Equal(Entry().EntryTimeUtc.AddDays(1), intent.QualifiedAtUtc);
        Assert.Equal(intent.QualifiedAtUtc.AddMinutes(15), intent.EntryDeadlineUtc);
        Assert.Equal(intent.QualifiedAtUtc.AddDays(20), intent.CloseByUtc);
        Assert.Equal(100, intent.Entry);
        Assert.Equal(.5m, intent.RiskMultiplier);
        Assert.False(Directory.Exists(Path.Combine(root, ".autotrading")));
    }

    [Fact]
    public void FeedRequiresConfirmedEntryAndStillOpenTrade()
    {
        var sink=Sink(true);
        var entry=Entry();
        sink.Vtb(entry,"id:open");
        var now=entry.EntryTimeUtc.AddDays(1).AddMinutes(1);
        var output=Path.Combine(root,"feed.json");
        TelegramDeliveryReceipt? receipt=null;
        Assert.True(sink.Publish([], [entry], _=>null, _=>receipt, output, now));
        JsonDocument ReadPayload() => JsonDocument.Parse(Convert.FromBase64String(
            JsonDocument.Parse(File.ReadAllText(output)).RootElement.GetProperty("payload").GetString()!));
        using(var data=ReadPayload()) Assert.Empty(data.RootElement.GetProperty("signals").EnumerateArray());
        receipt=new("id:open","Delivered",now,1,now);
        Assert.True(sink.Publish([], [entry], _=>null, _=>receipt, output, now));
        using(var data=ReadPayload()) Assert.Single(data.RootElement.GetProperty("signals").EnumerateArray());
        Assert.True(sink.Publish([], [entry with { Status="Closed" }], _=>null, _=>receipt, output, now));
        using(var data=ReadPayload()) Assert.Empty(data.RootElement.GetProperty("signals").EnumerateArray());
        Assert.True(sink.Publish([], [entry], _=>null, _=>receipt, output, now.AddHours(1)));
        using(var data=ReadPayload()) Assert.Empty(data.RootElement.GetProperty("signals").EnumerateArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
