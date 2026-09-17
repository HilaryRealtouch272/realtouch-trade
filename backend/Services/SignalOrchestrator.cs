using System.Collections.Concurrent;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

public record OrchestratorResult(bool Success, SignalResult? Signal, string? Reason, string InstrumentSymbol, string Timeframe);

// Wires the entire Strategy/ engine together into one real, live evaluation:
// fetch candles -> structure/condition -> zones/sweeps/key levels -> HTF
// alignment (section 7, via real context-timeframe fetches) -> setup models
// -> entry/stop/target -> confluence score -> risk sizing -> lifecycle ->
// SignalResult. Works against either provider (Bybit or Twelve Data) - the
// caller (Program.cs) decides how aggressively to pace/parallelize calls per
// provider's real constraints (Bybit is keyless/no quota; Twelve Data is
// quota- and rate-limited, so FX evaluations are paced, not parallel).
public class SignalOrchestrator(
    IEnumerable<IMarketDataProvider> providers,
    IEconomicCalendarProvider calendarProvider,
    INewsProvider newsProvider,
    IHostEnvironment env,
    ILogger<SignalOrchestrator> logger)
{
    private record CachedCondition(MarketCondition Condition, DateTime FetchedAtUtc);
    private record CalendarCacheEntry(CalendarResult Result, DateTime FetchedAtUtc);
    private record NewsCacheEntry(NewsResult Result, DateTime FetchedAtUtc);
    private record ContextCacheEntry(string Provider, string Symbol, Timeframe Tf, MarketCondition Condition, DateTime FetchedAtUtc);

    private const string StrategyVersion = "v1-strategy-engine";
    private const decimal PlaceholderAccountBalance = 10000m; // no real account/user data source exists yet

    // Warm-started from disk (see DiskCache) so a backend restart during
    // development doesn't force fresh calendar/news fetches for everything
    // already cached - Alpha Vantage in particular is only 25 requests/DAY
    // per key, which a few restarts can exhaust in minutes otherwise.
    private readonly string _calendarCachePath = Path.Combine(env.ContentRootPath, ".cache", "calendar-cache.json");
    private readonly string _newsCachePath = Path.Combine(env.ContentRootPath, ".cache", "news-cache.json");

    // Finnhub's calendar is fetched ONCE globally (not per-instrument) and
    // filtered per-instrument locally by currency - the veto-window check
    // only needs the event's scheduled time compared against "now", which
    // can be re-evaluated freshly from cached raw events without a new call.
    private CalendarResult? _cachedCalendar = DiskCache.Load<CalendarCacheEntry>(Path.Combine(env.ContentRootPath, ".cache", "calendar-cache.json"))?.Result;
    private DateTime _calendarCachedAtUtc = DiskCache.Load<CalendarCacheEntry>(Path.Combine(env.ContentRootPath, ".cache", "calendar-cache.json"))?.FetchedAtUtc ?? DateTime.MinValue;
    private static readonly TimeSpan CalendarCacheTtl = TimeSpan.FromHours(1);

    // Alpha Vantage's free tier is 25 requests/DAY per key - far tighter than
    // Twelve Data. Cached per instrument symbol with a long TTL; the
    // direction-relative Aligned/Conflict verdict is re-derived locally from
    // the cached raw items each time (direction can change scan-to-scan;
    // sentiment data does not need to be re-fetched to re-derive it).
    private readonly ConcurrentDictionary<string, (NewsResult Result, DateTime FetchedAtUtc)> _newsCache =
        new((DiskCache.Load<Dictionary<string, NewsCacheEntry>>(Path.Combine(env.ContentRootPath, ".cache", "news-cache.json")) ?? new())
            .ToDictionary(kv => kv.Key, kv => (kv.Value.Result, kv.Value.FetchedAtUtc)));
    private static readonly TimeSpan NewsCacheTtl = TimeSpan.FromHours(2);

    // Context timeframes (Daily/H4/Weekly) change slowly - re-fetching them on
    // every 15m/1H scan wastes most of a quota-limited provider's budget for
    // no benefit. Cached per (provider, symbol, timeframe) with a TTL scaled
    // to that timeframe's own candle duration, so a fast-moving main
    // timeframe can be polled often while its slow-moving context is reused.
    //
    // Warm-started from disk like calendar/news above - this one turned out
    // to matter even more: in the always-on server this dictionary lives for
    // the process's whole lifetime, but under GitHub Actions' --scan-once
    // mode every invocation is a brand-new process, so without disk
    // persistence this cache was NEVER warm and every single scheduled run
    // paid the full "main + up to 2 context fetches" cost per instrument -
    // multiplying real Twelve Data usage 2-3x and exhausting the 800/day/key
    // quota far faster than the cadence was actually designed for.
    private readonly string _contextCachePath = Path.Combine(env.ContentRootPath, ".cache", "context-cache.json");
    private readonly ConcurrentDictionary<(string Provider, string Symbol, Timeframe Tf), CachedCondition> _contextCache =
        new((DiskCache.Load<List<ContextCacheEntry>>(Path.Combine(env.ContentRootPath, ".cache", "context-cache.json")) ?? new())
            .ToDictionary(e => (e.Provider, e.Symbol, e.Tf), e => new CachedCondition(e.Condition, e.FetchedAtUtc)));

    private static TimeSpan ContextCacheTtl(Timeframe tf) =>
        TimeSpanMax(TimeframeConfig.Duration(tf) / 2, TimeSpan.FromMinutes(10));

    private static TimeSpan TimeSpanMax(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static string BuildProviderSymbol(InstrumentDefinition instrument) => instrument.Source switch
    {
        DataSource.Bybit => $"{instrument.FeedSymbol}:{instrument.BybitCategory}",
        DataSource.TwelveData => instrument.FeedSymbol,
        _ => instrument.FeedSymbol
    };

    private IMarketDataProvider SelectProvider(DataSource source) => source switch
    {
        DataSource.Bybit => providers.First(p => p.Name == "Bybit"),
        DataSource.TwelveData => providers.First(p => p.Name == "Twelve Data"),
        DataSource.Coinbase => providers.First(p => p.Name == "Coinbase"),
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    public async Task<OrchestratorResult> Evaluate(InstrumentDefinition instrument, Timeframe displayTimeframe)
    {
        var timeframeLabel = TimeframeIntervals.Label(displayTimeframe);
        try
        {
            var provider = SelectProvider(instrument.Source);
            var providerSymbol = BuildProviderSymbol(instrument);
            var mainCandles = await provider.GetCandlesAsync(instrument.Symbol, providerSymbol, displayTimeframe);
            if (!CandleQualityChecker.HasUsableData(mainCandles, minimumCompleted: 60))
                return new OrchestratorResult(false, null, "Data unavailable or stale - refusing to generate a signal on incomplete data", instrument.Symbol, timeframeLabel);

            var structure = StructureAnalyzer.Analyze(mainCandles, displayTimeframe);
            var condition = MarketConditionClassifier.Classify(mainCandles, displayTimeframe, structure);
            var obs = OrderBlockDetector.Detect(mainCandles, displayTimeframe, structure);
            var fvgs = FvgDetector.Detect(mainCandles, displayTimeframe);
            var willis = WillisZoneDetector.Detect(mainCandles, displayTimeframe, structure, obs, fvgs);
            var sweeps = LiquiditySweepDetector.Detect(mainCandles, displayTimeframe, structure);
            var keyLevels = KeyLevelCatalog.Build(mainCandles, displayTimeframe);

            // Pass 1: find which model applies at all, without HTF/R:R (direction isn't known yet).
            var candidate = TryModels(mainCandles, displayTimeframe, condition, structure, obs, fvgs, willis, sweeps, null, null);
            if (candidate is null)
                return new OrchestratorResult(false, null, $"No setup model's precondition is met (Market Condition: {condition})", instrument.Symbol, timeframeLabel);

            // Section 7: fetch real context timeframes now that we know the direction to check alignment against.
            var contextConditions = await BuildContextConditions(provider, instrument, providerSymbol, displayTimeframe, condition);
            var htfAlignment = TimeframeHierarchy.Evaluate(candidate.Direction, contextConditions);

            var tradePlan = EntryStopTargetCalculator.Compute(mainCandles, displayTimeframe, candidate.Direction, structure, obs, fvgs, willis, sweeps, keyLevels);

            // Sections 21-22: real calendar veto + news catalyst, cached (see
            // field comments above) to protect Alpha Vantage's tiny daily quota.
            var calendarVeto = await GetCalendarVeto(instrument.AffectedCurrencies);
            var newsCatalyst = await GetNewsCatalyst(instrument.Symbol, candidate.Direction);

            // Pass 2: re-evaluate with real HTF alignment, R:R and news veto now available.
            candidate = TryModels(mainCandles, displayTimeframe, condition, structure, obs, fvgs, willis, sweeps, htfAlignment, tradePlan?.RewardToRisk, calendarVeto.State);
            if (candidate is null)
                return new OrchestratorResult(false, null, "Setup model no longer applies on re-evaluation", instrument.Symbol, timeframeLabel);

            if (calendarVeto.State == CalendarVetoState.HardVeto)
            {
                return new OrchestratorResult(false, null,
                    $"Hard economic-calendar veto active: {calendarVeto.Reason}", instrument.Symbol, timeframeLabel);
            }

            var scoringInput = new ScoringInput(
                condition, candidate.Direction,
                LocationQualified: candidate.Requirements.Any(r => r.Description.Contains("discount") && r.Status == RequirementStatus.Met),
                LiquidityEventPresent: sweeps.Any(s => (s.Direction == SweepDirection.Bullish) == (candidate.Direction == SetupDirection.Long)),
                EntryStructureEvent: structure.Events.LastOrDefault()?.Type,
                ZoneSource: tradePlan?.Entry.ZoneSource,
                DisplacementAtEntry: tradePlan is not null && Displacement.IsDisplacementCandle(
                    mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList(),
                    mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList().Count - 1),
                RewardToRisk: tradePlan?.RewardToRisk,
                HtfAlignment: htfAlignment,
                CalendarVeto: calendarVeto.State,
                NewsCatalyst: newsCatalyst.State
            );
            var score = ConfluenceScorer.Score(scoringInput);

            if (tradePlan is null)
            {
                return new OrchestratorResult(false, null,
                    $"No valid entry/stop/target could be computed (score would be {score.TotalScore}/{score.Grade}) - no qualifying zone or an irrational stop distance",
                    instrument.Symbol, timeframeLabel);
            }

            var positionSize = RiskSizing.Compute(PlaceholderAccountBalance, 0m, score.Grade == "No setup" || score.Grade == "Watchlist" ? "B" : score.Grade,
                tradePlan.Entry.PreferredEntry, tradePlan.Stop.Price, isFx: instrument.Source == DataSource.TwelveData);

            var lastPrice = mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).Last().Close;
            var lifecycleState = SignalLifecycle.InitialState(score.TotalScore);

            var signal = SignalResultBuilder.Build(
                StrategyVersion, instrument.Symbol, instrument.Group, provider.Name,
                displayTimeframe, candidate, condition, tradePlan, score, positionSize,
                lifecycleState, keyLevels, lastPrice, DateTime.UtcNow,
                calendarVeto, newsCatalyst);

            return new OrchestratorResult(true, signal, null, instrument.Symbol, timeframeLabel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Signal orchestration failed for {Symbol} {Timeframe}", instrument.Symbol, timeframeLabel);
            return new OrchestratorResult(false, null, ex.Message, instrument.Symbol, timeframeLabel);
        }
    }

    private async Task<IReadOnlyDictionary<Timeframe, MarketCondition>> BuildContextConditions(
        IMarketDataProvider provider, InstrumentDefinition instrument, string providerSymbol, Timeframe displayTimeframe, MarketCondition mainCondition)
    {
        var contextTfs = TimeframeHierarchy.ContextTimeframes(displayTimeframe).Distinct().ToList();
        var result = new Dictionary<Timeframe, MarketCondition>();
        var now = DateTime.UtcNow;

        foreach (var tf in contextTfs)
        {
            if (tf == displayTimeframe) { result[tf] = mainCondition; continue; }

            var cacheKey = (provider.Name, instrument.Symbol, tf);
            if (_contextCache.TryGetValue(cacheKey, out var cached) && now - cached.FetchedAtUtc < ContextCacheTtl(tf))
            {
                result[tf] = cached.Condition;
                continue;
            }

            try
            {
                var candles = await provider.GetCandlesAsync(instrument.Symbol, providerSymbol, tf);
                if (!CandleQualityChecker.HasUsableData(candles, 60)) continue; // skip, don't fabricate a condition for stale context data
                var structure = StructureAnalyzer.Analyze(candles, tf);
                var condition = MarketConditionClassifier.Classify(candles, tf, structure);
                result[tf] = condition;
                _contextCache[cacheKey] = new CachedCondition(condition, now);
                PersistContextCache();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fetch context timeframe {Tf} for {Symbol}", tf, instrument.Symbol);
                // Fall back to a still-cached (even if expired) value rather than
                // dropping the context entirely on a transient fetch failure.
                if (cached is not null) result[tf] = cached.Condition;
            }
        }
        return result;
    }

    private static SetupCandidate? TryModels(
        IReadOnlyList<NormalizedCandle> candles, Timeframe timeframe, MarketCondition condition, StructureResult structure,
        IReadOnlyList<OrderBlock> obs, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<RealtouchWillisZone> willis,
        IReadOnlyList<LiquiditySweep> sweeps, HtfAlignment? htf, decimal? rr, CalendarVetoState? calendarVeto = null)
    {
        return SetupModels.EvaluateTrendContinuationPullback(candles, timeframe, condition, structure, obs, fvgs, willis, sweeps, htf, rr, calendarVeto)
            ?? SetupModels.EvaluateBreakoutAndRetest(candles, timeframe, condition, structure, obs, fvgs, rr, calendarVeto)
            ?? SetupModels.EvaluateLiquiditySweepReversal(candles, timeframe, condition, structure, obs, fvgs, sweeps, htf, calendarVeto)
            ?? SetupModels.EvaluateRangeBoundaryRejection(candles, timeframe, condition, structure, sweeps, rr, calendarVeto);
    }

    private void PersistContextCache() =>
        DiskCache.Save(_contextCachePath, _contextCache.Select(kv =>
            new ContextCacheEntry(kv.Key.Provider, kv.Key.Symbol, kv.Key.Tf, kv.Value.Condition, kv.Value.FetchedAtUtc)).ToList());

    private async Task<CalendarVetoResult> GetCalendarVeto(IReadOnlyCollection<string> affectedCurrencies)
    {
        var now = DateTime.UtcNow;
        if (_cachedCalendar is null || now - _calendarCachedAtUtc >= CalendarCacheTtl)
        {
            _cachedCalendar = await calendarProvider.GetUpcomingEventsAsync(now.AddHours(-2), now.AddDays(7));
            _calendarCachedAtUtc = now;
            DiskCache.Save(_calendarCachePath, new CalendarCacheEntry(_cachedCalendar, _calendarCachedAtUtc));
        }
        return EconomicCalendarVeto.Evaluate(_cachedCalendar, affectedCurrencies, now);
    }

    private async Task<NewsCatalystResult> GetNewsCatalyst(string canonicalSymbol, SetupDirection direction)
    {
        var now = DateTime.UtcNow;
        if (!_newsCache.TryGetValue(canonicalSymbol, out var cached) || now - cached.FetchedAtUtc >= NewsCacheTtl)
        {
            var result = await newsProvider.GetNewsAsync(canonicalSymbol);
            cached = (result, now);
            _newsCache[canonicalSymbol] = cached;
            DiskCache.Save(_newsCachePath, _newsCache.ToDictionary(kv => kv.Key, kv => new NewsCacheEntry(kv.Value.Result, kv.Value.FetchedAtUtc)));
        }
        return NewsCatalystEvaluator.Evaluate(cached.Result, direction);
    }
}
