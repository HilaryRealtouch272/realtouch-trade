using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Backtesting;

public record BacktestReport(
    string Symbol, string Timeframe, DateTime From, DateTime To, int Evaluations, int SignalsSeen, int TradesClosed, int TradesUnresolved,
    SegmentStats Overall, SegmentStats InSample, SegmentStats OutOfSample,
    IReadOnlyList<SegmentStats> WalkForward, IReadOnlyList<SegmentStats> ByModel, IReadOnlyList<string> Notes,
    IReadOnlyList<QualificationLogEntry> Trades);

// Replays the REAL signal engine (SignalOrchestrator) over historical candles: at
// each closed candle the engine sees only what had closed by then, decides exactly
// as it does live (same gates, thresholds, plan, scoring, cooldown), and every
// resulting trade is settled by the same TradeSimulator the live ledger uses, from
// the candles that followed. There is no second implementation of the rules to drift.
public class Backtester
{
    private class TempEnv(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Backtest";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Backtest";
    }

    private class NoCalendar : IEconomicCalendarProvider
    {
        public Task<CalendarResult> GetUpcomingEventsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
            Task.FromResult(new CalendarResult(false, "Not simulated in a backtest (no historical calendar)", Array.Empty<EconomicEvent>()));
    }

    private class NoNews : INewsProvider
    {
        public Task<NewsResult> GetNewsAsync(string canonicalSymbol, CancellationToken ct = default) =>
            Task.FromResult(new NewsResult(false, "Not simulated in a backtest (no historical news)", Array.Empty<NewsItem>()));
    }

    public async Task<BacktestReport> RunAsync(string symbol, Timeframe timeframe, int days, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var instrument = SetupCatalog.Instruments.FirstOrDefault(i => i.Symbol == symbol && i.Source == DataSource.Coinbase)
            ?? throw new ArgumentException($"{symbol} is not a Coinbase instrument in the catalog (backtests use Coinbase history).");
        if (timeframe is not (Timeframe.M15 or Timeframe.H1))
            throw new ArgumentException("Backtests support the 15m and 1H setup timeframes.");

        var (start, end, replay, m15, h1) = await PrepareAsync(symbol, instrument, days, progress, ct);
        var mainSpan = TimeConfigSpan(timeframe);

        var root = Path.Combine(Path.GetTempPath(), "rst-backtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = start;
        try
        {
            var env = new TempEnv(root);
            var orchestrator = new SignalOrchestrator(new IMarketDataProvider[] { replay }, new NoCalendar(), new NoNews(),
                new StrategyDiagnosticsStore(env), env, NullLogger<SignalOrchestrator>.Instance, () => now,
                useHigherTimeframeLevels: Environment.GetEnvironmentVariable("BACKTEST_HTF") != "0");

            return await ReplayAsync(orchestrator, replay, instrument, timeframe, mainSpan, start, end, m15, h1, v => now = v, progress, ct);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp only */ }
        }
    }

    private static TimeSpan TimeConfigSpan(Timeframe tf) => TimeframeConfig.Duration(tf);

    private static async Task<(DateTime Start, DateTime End, ReplayMarketDataProvider Replay, List<NormalizedCandle> M15, List<NormalizedCandle> H1)> PrepareAsync(
        string symbol, InstrumentDefinition instrument, int days, IProgress<string>? progress, CancellationToken ct)
    {
        var end = new DateTime(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.FromHours(1).Ticks, DateTimeKind.Utc);
        var start = end.AddDays(-days);

        using var http = new HttpClient();
        progress?.Report($"Fetching history for {symbol} ({days} days plus warm-up)...");
        var m15 = await CoinbaseHistory.FetchAsync(http, symbol, instrument.FeedSymbol, Timeframe.M15, 900, start.AddHours(-40), end, ct);
        var h1 = await CoinbaseHistory.FetchAsync(http, symbol, instrument.FeedSymbol, Timeframe.H1, 3600, start.AddDays(-14), end, ct);
        var d1 = await CoinbaseHistory.FetchAsync(http, symbol, instrument.FeedSymbol, Timeframe.Daily, 86400, start.AddDays(-140), end, ct);
        var h4 = ReplayMarketDataProvider.Aggregate(h1, TimeSpan.FromHours(1), TimeSpan.FromHours(4), Timeframe.H4);

        var replay = new ReplayMarketDataProvider();
        replay.AddSeries(symbol, Timeframe.M15, m15);
        replay.AddSeries(symbol, Timeframe.H1, h1);
        replay.AddSeries(symbol, Timeframe.H4, h4);
        replay.AddSeries(symbol, Timeframe.Daily, d1);
        return (start, end, replay, m15, h1);
    }

    // Shadow strategies over history: each candidate is entered at the shadow plan (market entry at
    // the confirming close, structural stop, 1R/2R/3R targets) and settled by the same simulator.
    // One trade open per shadow model at a time, with the same 4-candle cooldown as the live ledger.
    public async Task<BacktestReport> RunShadowAsync(string symbol, Timeframe timeframe, int days, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var instrument = SetupCatalog.Instruments.FirstOrDefault(i => i.Symbol == symbol && i.Source == DataSource.Coinbase)
            ?? throw new ArgumentException($"{symbol} is not a Coinbase instrument in the catalog.");
        var (start, end, replay, m15, _) = await PrepareAsync(symbol, instrument, days, progress, ct);
        var step = TimeConfigSpan(timeframe);
        var floor = SignalOrchestrator.MinStopCostFloor(symbol);
        InstrumentMetadataCatalog.TryGet(symbol, out var meta);

        var closed = new List<QualificationLogEntry>();
        var open = new Dictionary<ShadowModel, QualificationLogEntry>();
        var lastClose = new Dictionary<ShadowModel, DateTime>();
        var evaluations = 0;

        for (var t = start.Add(step); t <= end; t += step)
        {
            ct.ThrowIfCancellationRequested();
            replay.AsOf = t;
            var visible15 = ReplayMarketDataProvider.VisibleAt(m15, t);

            foreach (var model in open.Keys.ToList())
            {
                var e = TradeSimulator.ApplyCandleSequence(open[model], visible15.Where(c => c.OpenTimeUtc >= open[model].QualifiedAtUtc).ToList(), t);
                if (TradeSimulator.IsOpen(e.Status)) { open[model] = e; continue; }
                closed.Add(e); open.Remove(model); lastClose[model] = e.ClosedAtUtc ?? t;
            }

            evaluations++;
            var candles = await replay.GetCandlesAsync(symbol, instrument.FeedSymbol, timeframe, ct);
            var structure = StructureAnalyzer.Analyze(candles, timeframe);
            var candidates = ShadowStrategies.Evaluate(candles, timeframe, structure,
                OrderBlockDetector.Detect(candles, timeframe, structure), FvgDetector.Detect(candles, timeframe),
                KeyLevelCatalog.Build(candles, timeframe), floor);

            foreach (var cand in candidates)
            {
                if (open.ContainsKey(cand.Model)) continue;
                if (lastClose.TryGetValue(cand.Model, out var lc) && t - lc < step * SignalLogService.CooldownCandles) continue;
                open[cand.Model] = ShadowEntry(symbol, timeframe, cand, t, meta);
            }
        }

        var notes = new List<string>
        {
            "SHADOW-MODE RESULTS: these strategies never alert and never enter the live ledger; this is their validation run.",
            "Market entry at the confirming candle close, structural stop, fixed 1R/2R/3R targets; same simulator, costs and cooldown as the live ledger.",
            "The Zone Mitigation strategy uses the setup timeframe's own zones (higher-timeframe zone detection is not built).",
            "Calendar and news are not simulated. Fewer than 30 trades per model is provisional; promotion needs 50-100 plus out-of-sample validation.",
        };
        var (inSample, outOfSample) = BacktestStats.InOutOfSample(closed);
        var byModel = closed.GroupBy(c => c.SetupModel).Select(g => BacktestStats.Segment(g.Key, g.ToList())).OrderByDescending(s => s.Trades).ToList();
        return new BacktestReport(symbol, TimeframeIntervals.Label(timeframe) + " (shadow)", start, end, evaluations, closed.Count + open.Count, closed.Count, open.Count,
            BacktestStats.Segment("Overall", closed), inSample, outOfSample, BacktestStats.WalkForward(closed, 4), byModel, notes, closed);
    }

    private static QualificationLogEntry ShadowEntry(string symbol, Timeframe tf, ShadowCandidate c, DateTime at, InstrumentMetadata? meta)
    {
        var risk = Math.Abs(c.Entry - c.Stop);
        return new QualificationLogEntry(
            Id: Guid.NewGuid().ToString("N"), Symbol: symbol, Timeframe: TimeframeIntervals.Label(tf), Direction: c.Direction.ToString(),
            SetupModel: c.Model.ToString(), Grade: "Shadow", Score: c.Score, Entry: c.Entry, Stop: c.Stop, Tp1: c.Tp1, Tp2: c.Tp2, Tp3: c.Tp3,
            RewardToRisk: 2m, QualifiedAtUtc: at, TrackingExpiryUtc: at + TimeframeConfig.Duration(tf) * 30,
            Status: "Open", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null,
            AssetClass: meta?.AssetClass ?? "", FinalStop: c.Stop, RiskPercent: 1m, RiskAmount: 100m,
            PositionSize: risk > 0 ? 100m / risk : 0m,
            EntrySpreadUnits: meta?.DefaultSpreadUnits ?? 0m, SlippageUnits: meta?.DefaultSlippageUnits ?? 0m,
            Exits: Array.Empty<ExitFill>(), CommissionUnits: meta?.CommissionUnits ?? 0m, EntryFillPrice: c.Entry, EntryTimeUtc: at);
    }

    private static async Task<BacktestReport> ReplayAsync(
        SignalOrchestrator orchestrator, ReplayMarketDataProvider replay, InstrumentDefinition instrument, Timeframe tf, TimeSpan step,
        DateTime start, DateTime end, IReadOnlyList<NormalizedCandle> m15, IReadOnlyList<NormalizedCandle> h1,
        Action<DateTime> setNow, IProgress<string>? progress, CancellationToken ct)
    {
        var symbol = instrument.Symbol;
        var tfLabel = TimeframeIntervals.Label(tf);
        var cooldown = step * SignalLogService.CooldownCandles;
        var closed = new List<QualificationLogEntry>();
        QualificationLogEntry? open = null;
        (string Direction, DateTime At)? lastClose = null;
        int evaluations = 0, signals = 0;
        var notes = new List<string>
        {
            "Economic calendar and news are not simulated (no historical feed): those checks are Unavailable, which never blocks, so live results with real vetoes would be somewhat more selective.",
            "4H candles are built from 1H on UTC-aligned boundaries; live 4H bars come from sequential grouping and may be offset.",
            "Same-candle stop/target ordering on 15m trades cannot be resolved without finer data: it is settled stop-first and counted as uncertain.",
            "Setups are entered at the plan's preferred entry when the engine reports them Triggered, exactly as the live ledger does; there is no cost model beyond the instrument's configured spread, slippage and commission.",
        };

        var totalSteps = (int)((end - start).Ticks / step.Ticks);
        var stepIndex = 0;
        for (var t = start.Add(step); t <= end; t += step, stepIndex++)
        {
            ct.ThrowIfCancellationRequested();
            setNow(t);
            replay.AsOf = t;

            if (open is not null)
            {
                var visible = ReplayMarketDataProvider.VisibleAt(m15, t);
                var fine = tf == Timeframe.M15 ? null : (IReadOnlyList<NormalizedCandle>)visible;
                open = TradeSimulator.ApplyCandleSequence(open, visible.Where(c => c.OpenTimeUtc >= open.QualifiedAtUtc).ToList(), t, fine);
                if (!TradeSimulator.IsOpen(open.Status))
                {
                    closed.Add(open);
                    lastClose = (open.Direction, open.ClosedAtUtc ?? t);
                    open = null;
                }
            }

            if (open is null)
            {
                evaluations++;
                var result = await orchestrator.Evaluate(instrument, tf);
                if (result.Success && result.Signal is { } signal && signal.Grade is "A+" or "A" or "B" && result.Signal!.Triggered)
                {
                    signals++;
                    var inCooldown = lastClose is { } lc && lc.Direction == signal.Direction.ToString() && t - lc.At < cooldown;
                    if (!inCooldown) open = SignalLogService.CreateEntry(result, t);
                }
            }

            if (stepIndex % 200 == 0) progress?.Report($"  {stepIndex}/{totalSteps} steps, {closed.Count} trades closed");
        }

        // A trade still open at the end has no outcome yet: it is reported, not scored.
        var unresolved = open is null ? 0 : 1;

        var (inSample, outOfSample) = BacktestStats.InOutOfSample(closed);
        var byModel = closed.GroupBy(c => c.SetupModel).Select(g => BacktestStats.Segment(g.Key, g.ToList())).OrderByDescending(s => s.Trades).ToList();

        return new BacktestReport(symbol, tfLabel, start, end, evaluations, signals, closed.Count, unresolved,
            BacktestStats.Segment("Overall", closed), inSample, outOfSample,
            BacktestStats.WalkForward(closed, 4), byModel, notes, closed);
    }
}
