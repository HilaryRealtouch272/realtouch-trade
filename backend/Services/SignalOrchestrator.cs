using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// AllEvaluations: every model's independent StrategyEvaluation for this scan
// (section 4.7-4.8: qualified AND rejected candidates are both stored) -
// optional/last so existing positional and named call sites are unaffected.
// LivePrice: the real current close this scan actually fetched, attached
// regardless of whether a NEW candidate qualified. A trade already open in
// the ledger from an earlier scan needs its stop/targets checked against
// real price on every scan it's still open, not only on scans where a
// fresh setup happens to qualify - the moment price approaches that
// trade's own stop is usually exactly when the ORIGINAL qualifying
// structure breaks, so "no signal this scan" and "the open trade just got
// stopped out" are not mutually exclusive. Null only when no usable candle
// data was fetched at all (nothing to price against).
// Candles: the same completed candles LivePrice was derived from, kept in
// full so the ledger can walk each one's real High/Low in order rather
// than only ever comparing against the latest Close - a scan-interval
// price check that only looks at "where is price right now" can miss a
// stop that was crossed and later reversed away from within the same gap
// between checks. Same null-only-on-no-data rule as LivePrice. JsonIgnore:
// this is consumed entirely in-process (SignalLogService, same scan) and
// must never reach signals.json - that file is both the public GitHub
// Pages payload the frontend fetches AND what --scan-once reads back as
// "prior results" for gated timeframes, and dozens of full OHLC candles
// per instrument/timeframe on every ~15-minute run would bloat both for
// no benefit; nothing downstream of that file ever needs raw candles.
public record OrchestratorResult(bool Success, SignalResult? Signal, string? Reason, string InstrumentSymbol, string Timeframe, IReadOnlyList<StrategyEvaluation>? AllEvaluations = null, decimal? LivePrice = null, [property: JsonIgnore] IReadOnlyList<Models.NormalizedCandle>? Candles = null);

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
    StrategyDiagnosticsStore diagnosticsStore,
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

    private static readonly SetupModelType[] AllModels =
    {
        SetupModelType.TrendContinuationPullback, SetupModelType.BreakoutAndRetest,
        SetupModelType.LiquiditySweepReversal, SetupModelType.RangeBoundaryRejection
    };

    // Section 4: every model runs independently on every scan - no
    // first-match short-circuit. The old code was `Trend ?? Breakout ??
    // Reversal ?? Range`, so ANY qualifying Trend candidate silently
    // prevented the other three from ever being tried, and a rejected
    // candidate left no record at all. Now all four always run, all four
    // are recorded (qualified or not - see diagnosticsStore.Record below),
    // and the highest-scoring QUALIFIED one becomes the tracked trade; the
    // others are secondary classifications only (section 4.9/4.10 - one
    // tracked position per symbol+timeframe, never a duplicate).
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

            // Computed once, right after the data-quality gate, and threaded
            // into every return below (success or not) - see LivePrice's and
            // Candles' doc comments on OrchestratorResult for why this can't
            // wait until only the success path needs it.
            var completedCandles = mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList();
            var lastPrice = completedCandles[^1].Close;

            var structure = StructureAnalyzer.Analyze(mainCandles, displayTimeframe);
            var condition = MarketConditionClassifier.Classify(mainCandles, displayTimeframe, structure);
            var obs = OrderBlockDetector.Detect(mainCandles, displayTimeframe, structure);
            var fvgs = FvgDetector.Detect(mainCandles, displayTimeframe);
            var willis = WillisZoneDetector.Detect(mainCandles, displayTimeframe, structure, obs, fvgs);
            var sweeps = LiquiditySweepDetector.Detect(mainCandles, displayTimeframe, structure);
            var keyLevels = KeyLevelCatalog.Build(mainCandles, displayTimeframe);

            // Common gate (section 5): fetched once, symbol-wide - not model
            // specific, and never included in any model's own weight table.
            var calendarVeto = await GetCalendarVeto(instrument.AffectedCurrencies);

            var evaluations = new List<StrategyEvaluation>();
            var contextCache = new Dictionary<SetupDirection, HtfAlignment>();
            foreach (var model in AllModels)
            {
                var evaluation = await EvaluateOneModel(
                    model, instrument, provider, providerSymbol, displayTimeframe, mainCandles,
                    structure, condition, obs, fvgs, willis, sweeps, keyLevels, calendarVeto, contextCache);
                evaluations.Add(evaluation);
            }

            diagnosticsStore.Record(instrument.Symbol, timeframeLabel, DateTime.UtcNow, condition, evaluations);

            var qualified = evaluations.Where(e => e.Qualified).OrderByDescending(e => e.Score).ToList();
            if (qualified.Count == 0)
            {
                var best = evaluations.OrderByDescending(e => e.Score).FirstOrDefault();
                var reason = best is null
                    ? $"No setup model's precondition is met (Market Condition: {condition})"
                    : $"No model qualified this scan - closest was {best.StrategyId} at {best.Score}/{best.Threshold} (Market Condition: {condition})";
                return new OrchestratorResult(false, null, reason, instrument.Symbol, timeframeLabel, evaluations, lastPrice, completedCandles);
            }

            var primary = qualified[0];
            var candidate = primary.Candidate!;
            var htfAlignment = contextCache.TryGetValue(candidate.Direction, out var htf) ? htf : (HtfAlignment?)null;
            var tradePlan = await BuildTradePlan(model: primary.StrategyId, mainCandles, displayTimeframe, structure, obs, fvgs, willis, sweeps, keyLevels, candidate.Direction);

            if (tradePlan is null)
            {
                return new OrchestratorResult(false, null,
                    $"{primary.StrategyId}: No valid entry/stop/target could be computed (score {primary.Score}/{primary.Threshold}) - no qualifying zone or an irrational stop distance",
                    instrument.Symbol, timeframeLabel, evaluations, lastPrice, completedCandles);
            }

            if (calendarVeto.State == CalendarVetoState.HardVeto)
            {
                return new OrchestratorResult(false, null,
                    $"Hard economic-calendar veto active: {calendarVeto.Reason}", instrument.Symbol, timeframeLabel, evaluations, lastPrice, completedCandles);
            }

            var newsCatalyst = await GetNewsCatalyst(instrument.Symbol, candidate.Direction);

            // LiquiditySweepReversal is a counter-trend reversal call by nature -
            // real, elevated risk when it also runs against the broader Weekly
            // trend. Halving the default risk rather than skipping the trade:
            // the setup can still be genuinely valid HTF-conflicting or not,
            // just sized for the added risk.
            var effectiveGrade = primary.Grade == "No setup" || primary.Grade == "Tracking" ? "B" : primary.Grade;
            var requestedRiskPercent = primary.StrategyId == SetupModelType.LiquiditySweepReversal && htfAlignment == HtfAlignment.Conflicting
                ? RiskSizing.DefaultRiskPercent(effectiveGrade) / 2
                : 0m;
            var positionSize = RiskSizing.Compute(PlaceholderAccountBalance, requestedRiskPercent, effectiveGrade,
                tradePlan.Entry.PreferredEntry, tradePlan.Stop.Price, isFx: instrument.Source == DataSource.TwelveData);

            var scoreResult = new ConfluenceScoreResult(primary.Score, primary.Grade, primary.ConfluenceFamilies);
            var lifecycleState = SignalLifecycle.InitialState(primary.Score);

            var signal = SignalResultBuilder.Build(
                StrategyVersion, instrument.Symbol, instrument.Group, provider.Name,
                displayTimeframe, candidate, condition, tradePlan, scoreResult, positionSize,
                lifecycleState, keyLevels, lastPrice, DateTime.UtcNow,
                calendarVeto, newsCatalyst);

            return new OrchestratorResult(true, signal, null, instrument.Symbol, timeframeLabel, evaluations, lastPrice, completedCandles);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Signal orchestration failed for {Symbol} {Timeframe}", instrument.Symbol, timeframeLabel);
            return new OrchestratorResult(false, null, ex.Message, instrument.Symbol, timeframeLabel);
        }
    }

    private async Task<TradePlan?> BuildTradePlan(
        SetupModelType model, IReadOnlyList<NormalizedCandle> mainCandles, Timeframe displayTimeframe, StructureResult structure,
        IReadOnlyList<OrderBlock> obs, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<RealtouchWillisZone> willis,
        IReadOnlyList<LiquiditySweep> sweeps, IReadOnlyList<KeyLevel> keyLevels, SetupDirection direction)
    {
        await Task.CompletedTask;
        // Range Boundary Rejection needs its own plan (target = equilibrium
        // or opposite boundary, section 6.4) - the pullback planner only
        // knows how to plan off an order block/FVG/Willis Zone, which a bare
        // range boundary often doesn't have sitting on it.
        if (model == SetupModelType.RangeBoundaryRejection)
        {
            var lastCloseTime = mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).Last().CloseTimeUtc;
            return RangeTradePlan.Build(mainCandles, displayTimeframe, direction, lastCloseTime);
        }
        return EntryStopTargetCalculator.Compute(mainCandles, displayTimeframe, direction, structure, obs, fvgs, willis, sweeps, keyLevels);
    }

    // One model's full, independent evaluation: detect -> HTF (if a
    // candidate was found) -> plan -> score against THAT model's own
    // weight table and threshold. Always returns a StrategyEvaluation -
    // never null, never skipped, per section 4's "each evaluator must
    // return a result" rule.
    private async Task<StrategyEvaluation> EvaluateOneModel(
        SetupModelType model, InstrumentDefinition instrument, IMarketDataProvider provider, string providerSymbol, Timeframe displayTimeframe,
        IReadOnlyList<NormalizedCandle> mainCandles, StructureResult structure, MarketCondition condition,
        IReadOnlyList<OrderBlock> obs, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<RealtouchWillisZone> willis,
        IReadOnlyList<LiquiditySweep> sweeps, IReadOnlyList<KeyLevel> keyLevels, CalendarVetoResult calendarVeto,
        Dictionary<SetupDirection, HtfAlignment> htfCache)
    {
        SetupCandidate? DetectPass1() => model switch
        {
            SetupModelType.TrendContinuationPullback => SetupModels.EvaluateTrendContinuationPullback(mainCandles, displayTimeframe, condition, structure, obs, fvgs, willis, sweeps),
            SetupModelType.BreakoutAndRetest => SetupModels.EvaluateBreakoutAndRetest(mainCandles, displayTimeframe, structure, obs, fvgs),
            SetupModelType.LiquiditySweepReversal => SetupModels.EvaluateLiquiditySweepReversal(mainCandles, displayTimeframe, condition, structure, obs, fvgs, sweeps, keyLevels: keyLevels),
            SetupModelType.RangeBoundaryRejection => SetupModels.EvaluateRangeBoundaryRejection(mainCandles, displayTimeframe, condition, structure, sweeps),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };

        var pass1 = DetectPass1();
        if (pass1 is null)
            return new StrategyEvaluation(model, StrategyVersion, false, false, 0, "No setup", ThresholdFor(model),
                Array.Empty<ConfluenceFamilyScore>(), new[] { ReasonCode.TREND_CONDITION_NOT_CONFIRMED }, Array.Empty<ReasonCode>(), null);

        var direction = pass1.Direction;
        if (!htfCache.TryGetValue(direction, out var htfAlignment))
        {
            var contextConditions = await BuildContextConditions(provider, instrument, providerSymbol, displayTimeframe, condition);
            htfAlignment = TimeframeHierarchy.Evaluate(direction, contextConditions);
            htfCache[direction] = htfAlignment;
        }

        var tradePlan = await BuildTradePlan(model, mainCandles, displayTimeframe, structure, obs, fvgs, willis, sweeps, keyLevels, direction);

        SetupCandidate? pass2 = model switch
        {
            SetupModelType.TrendContinuationPullback => SetupModels.EvaluateTrendContinuationPullback(mainCandles, displayTimeframe, condition, structure, obs, fvgs, willis, sweeps, htfAlignment, tradePlan?.RewardToRisk, calendarVeto.State),
            SetupModelType.BreakoutAndRetest => SetupModels.EvaluateBreakoutAndRetest(mainCandles, displayTimeframe, structure, obs, fvgs, tradePlan?.RewardToRisk, calendarVeto.State, direction),
            SetupModelType.LiquiditySweepReversal => SetupModels.EvaluateLiquiditySweepReversal(mainCandles, displayTimeframe, condition, structure, obs, fvgs, sweeps, htfAlignment, calendarVeto.State, keyLevels),
            SetupModelType.RangeBoundaryRejection => SetupModels.EvaluateRangeBoundaryRejection(mainCandles, displayTimeframe, condition, structure, sweeps, tradePlan?.RewardToRisk, calendarVeto.State, tradePlan?.Targets.Tp2),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };

        var candidate = pass2 ?? pass1;
        var gatesPassed = candidate.Qualified && tradePlan is not null;
        var failedGates = candidate.Requirements.Where(r => r.Status == RequirementStatus.NotMet).Select(r => MapReasonCode(r.Description)).ToList();
        if (tradePlan is null) failedGates.Add(ReasonCode.ZONE_NOT_FOUND);
        var warnings = candidate.Requirements.Where(r => r.Status == RequirementStatus.NotEvaluated).Select(r => MapReasonCode(r.Description)).ToList();

        var completedForDisplacement = mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList();
        var displacementAtEntry = tradePlan is not null && completedForDisplacement.Count > 0 &&
            Displacement.IsDisplacementCandle(completedForDisplacement, completedForDisplacement.Count - 1);
        var liquidityEventPresent = sweeps.Any(s => (s.Direction == SweepDirection.Bullish) == (direction == SetupDirection.Long));
        var entryStructureEvent = structure.Events.LastOrDefault(e =>
            direction == SetupDirection.Long ? e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch
                                              : e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch)?.Type;

        ConfluenceScoreResult score = model switch
        {
            SetupModelType.TrendContinuationPullback => ModelScoring.ScoreTrendContinuation(
                htfAlignment, candidate.Requirements.Any(r => r.Description.Contains("discount") && r.Status == RequirementStatus.Met),
                tradePlan?.Entry.ZoneSource, liquidityEventPresent, entryStructureEvent, displacementAtEntry,
                tradePlan?.RewardToRisk, calendarVeto.State, null),
            SetupModelType.BreakoutAndRetest => ModelScoring.ScoreBreakoutAndRetest(
                ModelEvidenceBuilder.BuildBreakout(mainCandles, displayTimeframe, structure, obs, fvgs, direction),
                displacementAtEntry, tradePlan?.RewardToRisk, calendarVeto.State, null),
            SetupModelType.LiquiditySweepReversal => ModelScoring.ScoreLiquiditySweepReversal(
                ModelEvidenceBuilder.BuildReversal(mainCandles, structure, sweeps, obs, fvgs, keyLevels, direction),
                fvgs.Any(f => (f.Direction == FvgDirection.Bullish) == (direction == SetupDirection.Long) && f.Status != MitigationStatus.Invalidated) ||
                obs.Any(o => (o.Direction == OrderBlockDirection.Bullish) == (direction == SetupDirection.Long) && OrderBlockDetector.IsValid(o)),
                tradePlan?.RewardToRisk, calendarVeto.State, null),
            SetupModelType.RangeBoundaryRejection => ModelScoring.ScoreRangeBoundaryRejection(
                ModelEvidenceBuilder.BuildRange(mainCandles, sweeps, structure, direction),
                tradePlan?.RewardToRisk, calendarVeto.State, null),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };

        return new StrategyEvaluation(model, StrategyVersion, true, gatesPassed, score.TotalScore, score.Grade, ThresholdFor(model),
            score.Families, failedGates, warnings, gatesPassed ? candidate : null, direction);
    }

    private static int ThresholdFor(SetupModelType model) => model switch
    {
        SetupModelType.TrendContinuationPullback => 75,
        SetupModelType.BreakoutAndRetest => 75,
        SetupModelType.LiquiditySweepReversal => 78,
        SetupModelType.RangeBoundaryRejection => 78,
        _ => 75
    };

    // Free-text requirement descriptions -> structured reason codes (section
    // 9). An honest best-effort keyword mapping, not a perfect taxonomy -
    // the description string itself is always still available in the raw
    // diagnostics record for anyone who needs the exact wording.
    private static ReasonCode MapReasonCode(string description)
    {
        var d = description.ToLowerInvariant();
        if (d.Contains("htf") || d.Contains("higher-timeframe") || d.Contains("weekly") || d.Contains("directional alignment")) return ReasonCode.TREND_ALIGNMENT_MISSING;
        if (d.Contains("trending market condition")) return ReasonCode.TREND_CONDITION_NOT_CONFIRMED;
        if (d.Contains("discount") || d.Contains("premium")) return ReasonCode.LOCATION_NOT_QUALIFIED;
        if (d.Contains("order block") || d.Contains("fvg") || d.Contains("willis") || d.Contains("zone")) return ReasonCode.ZONE_NOT_FOUND;
        if (d.Contains("sweep") && d.Contains("choch")) return ReasonCode.SWEEP_CLOSE_NOT_CONFIRMED;
        if (d.Contains("sweep")) return ReasonCode.SWEEP_NOT_CONFIRMED;
        if (d.Contains("choch")) return ReasonCode.CHOCH_MISSING;
        if (d.Contains("range") && d.Contains("boundary")) return ReasonCode.RANGE_BOUNDARY_NOT_REACHED;
        if (d.Contains("range")) return ReasonCode.RANGE_NOT_ESTABLISHED;
        if (d.Contains("equilibrium")) return ReasonCode.ENTRY_TOO_CLOSE_TO_EQUILIBRIUM;
        if (d.Contains("breakout")) return ReasonCode.BREAKOUT_NOT_CONFIRMED;
        if (d.Contains("retest") && d.Contains("controlled")) return ReasonCode.CONTROLLED_RETEST_MISSING;
        if (d.Contains("retest")) return ReasonCode.RETEST_NOT_REACHED;
        if (d.Contains("reward-to-risk") || d.Contains("r:r")) return ReasonCode.REWARD_RISK_TOO_LOW;
        if (d.Contains("news") || d.Contains("veto")) return ReasonCode.NEWS_VETO_ACTIVE;
        if (d.Contains("structure") || d.Contains("bos")) return ReasonCode.STRUCTURE_CONFIRMATION_MISSING;
        return ReasonCode.STRUCTURE_CONFIRMATION_MISSING;
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
