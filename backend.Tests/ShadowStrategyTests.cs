using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class ShadowStrategyTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Span = TimeSpan.FromMinutes(15);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rst-shadow-" + Guid.NewGuid().ToString("N"));

    private static NormalizedCandle C(int i, decimal open, decimal high, decimal low, decimal close) =>
        new("T/USD", "T-USD", "Test", Timeframe.M15, T0 + Span * i, T0 + Span * (i + 1), open, high, low, close, 1m, true, T0, DataQuality.Ok);

    // A choppy start (so the ATR baseline is real), then whatever the test appends.
    private static List<NormalizedCandle> Choppy(int count)
    {
        var list = new List<NormalizedCandle>();
        for (var i = 0; i < count; i++)
        {
            var up = i % 2 == 0;
            list.Add(C(i, up ? 99m : 101m, 101.5m, 98.5m, up ? 101m : 99m));
        }
        return list;
    }

    private static KeyLevel Level(decimal price) =>
        new(KeyLevelType.ConfirmedSwingHigh, Timeframe.M15, price, price, T0, T0, false, false, 0);

    [Fact]
    public void AFailedBreakoutAboveALevelThatIsReclaimedWithDisplacementIsAShadowShort()
    {
        var c = Choppy(60);                                   // flat around 100, small bodies
        c.Add(C(60, 104m, 106.5m, 103.5m, 106m));             // closes above the 105 level: the break
        c.Add(C(61, 106m, 106.2m, 103.8m, 104m));             // closes back below within 3 candles: the reclaim
        c.Add(C(62, 104m, 104.1m, 99.5m, 100m));              // opposing displacement
        c.Add(C(63, 100m, 104.9m, 99.8m, 104.2m));            // retest of the level from below, held

        var atr = Indicators.Atr(c, 14);
        var found = ShadowStrategies.FailedBreakout(c, atr, new[] { Level(105m) }, SetupDirection.Short, 0m);

        Assert.NotNull(found);
        Assert.Equal(ShadowModel.FailedBreakoutReclaim, found!.Model);
        Assert.Equal(SetupDirection.Short, found.Direction);
        Assert.True(found.Stop > found.Entry);                 // stop sits beyond the failed break's high
        Assert.True(found.Tp1 < found.Entry && found.Tp2 < found.Tp1 && found.Tp3 < found.Tp2);
        Assert.True(found.Score >= ShadowStrategies.MinScore);
    }

    [Fact]
    public void ABreakThatNeverFailsIsNotAFailedBreakout()
    {
        var c = Choppy(60);
        c.Add(C(60, 104m, 107m, 103.5m, 106.5m));
        c.Add(C(61, 106.5m, 108m, 106m, 107.5m));             // holds above the level: a real breakout
        c.Add(C(62, 107.5m, 109m, 107m, 108.5m));
        c.Add(C(63, 108.5m, 110m, 108m, 109.5m));

        Assert.Null(ShadowStrategies.FailedBreakout(c, Indicators.Atr(c, 14), new[] { Level(105m) }, SetupDirection.Short, 0m));
    }

    [Fact]
    public void AContractionThenADecisiveExpansionCloseAndAHeldRetestIsAShadowLong()
    {
        var c = Choppy(30);
        for (var i = 30; i < 84; i++) c.Add(C(i, 100m, 100.25m, 99.95m, 100.1m));   // tight coil
        c.Add(C(84, 100.1m, 100.3m, 99.95m, 100.15m));
        c.Add(C(85, 100.15m, 100.3m, 100m, 100.2m));
        c.Add(C(86, 100.3m, 102m, 100.3m, 101.8m));                                   // decisive expansion close
        c.Add(C(87, 101.8m, 102m, 101.4m, 101.6m));
        c.Add(C(88, 101.6m, 101.7m, 100.35m, 101.3m));                                // retest of the ~100.3 edge, held
        c.Add(C(89, 101.3m, 101.6m, 101.2m, 101.5m));

        var found = ShadowStrategies.ContractionBreakout(c, Indicators.Atr(c, 14), Array.Empty<KeyLevel>(), SetupDirection.Long, 0m);

        Assert.NotNull(found);
        Assert.Equal(ShadowModel.VolatilityContractionBreakoutRetest, found!.Model);
        Assert.True(found.Stop < found.Entry);
    }

    [Fact]
    public void NoContractionMeansNoContractionBreakout()
    {
        var c = Choppy(90);                                                           // constant volatility throughout

        Assert.Null(ShadowStrategies.ContractionBreakout(c, Indicators.Atr(c, 14), Array.Empty<KeyLevel>(), SetupDirection.Long, 0m));
    }

    private class Env : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    [Fact]
    public void ASetupThatStaysValidIsRecordedOnceUntilFourCandlesHavePassed()
    {
        var store = new ShadowCandidateStore(new Env { ContentRootPath = _root });
        var cand = new ShadowCandidate(ShadowModel.FailedBreakoutReclaim, SetupDirection.Short, 90, new[] { "e" }, Array.Empty<string>(),
            104m, 106m, 102m, 100m, 98m, T0);

        store.Record("BTC/USDT", "15m", T0, Span, new[] { cand });
        store.Record("BTC/USDT", "15m", T0.AddMinutes(15), Span, new[] { cand });
        Assert.Single(store.GetAll());

        store.Record("BTC/USDT", "15m", T0.AddMinutes(75), Span, new[] { cand with { DetectedAtUtc = T0.AddMinutes(75) } });
        Assert.Equal(2, store.GetAll().Count);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }
}
