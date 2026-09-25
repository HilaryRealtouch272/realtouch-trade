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
    ILogger<SignalOrchestrator> logger,
    // The clock the engine reads. Live use leaves it null (real UTC); a backtest supplies
    // the replay's current time so caches, expiry and freshness behave as they did then.
    Func<DateTime>? clock = null,
    // When true (the default) the trade plan's targets also see the higher-timeframe key
    // levels, so a nearby Daily/4H barrier caps TP2 and, through the R:R gate, rejects a
    // setup whose path is blocked. Off only to compare against the old behaviour.
    bool useHigherTimeframeLevels = true,
    // Shadow strategies are evaluated and recorded here, never alerted or entered.
    ShadowCandidateStore? shadowStore = null)
{
    private DateTime Now() => clock?.Invoke() ?? DateTime.UtcNow;

    private record CachedCondition(MarketCondition Condition, DateTime FetchedAtUtc, IReadOnlyList<KeyLevel>? Levels = null);
    private record CalendarCacheEntry(CalendarResult Result, DateTime FetchedAtUtc);
    private record NewsCacheEntry(NewsResult Result, DateTime FetchedAtUtc);
    private record ContextCacheEntry(string Provider, string Symbol, Timeframe Tf, MarketCondition Condition, DateTime FetchedAtUtc, IReadOnlyList<KeyLevel>? Levels = null);

    // What one scan learns about the higher timeframes, shared by every model it evaluates.
    private class ScanContext
    {
        public Dictionary<SetupDirection, HtfAlignment> Htf { get; } = new();
        public List<KeyLevel> Levels { get; } = new();
        public List<Timeframe> Missing { get; } = new();
    }

    private record ContextEvidence(IReadOnlyDictionary<Timeframe, MarketCondition> Conditions, IReadOnlyList<KeyLevel> Levels, IReadOnlyList<Timeframe> Missing);

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
            .ToDictionary(e => (e.Provider, e.Symbol, e.Tf), e => new CachedCondition(e.Condition, e.FetchedAtUtc, e.Levels)));

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
            var scanContext = new ScanContext();
            foreach (var model in AllModels)
            {
                var evaluation = await EvaluateOneModel(
                    model, instrument, provider, providerSymbol, displayTimeframe, mainCandles,
                    structure, condition, obs, fvgs, willis, sweeps, keyLevels, calendarVeto, scanContext);
                evaluations.Add(evaluation);
            }

            var primary = SelectPrimary(evaluations, condition);

            if (shadowStore is not null)
            {
                var shadowCandidates = ShadowStrategies.Evaluate(mainCandles, displayTimeframe, structure, obs, fvgs, keyLevels, MinStopCostFloor(instrument.Symbol));
                shadowStore.Record(instrument.Symbol, timeframeLabel, Now(), TimeframeConfig.Duration(displayTimeframe), shadowCandidates);
            }

            var scanNow = Now();
            var freshness = completedCandles.Count > 0 ? (scanNow - completedCandles[^1].CloseTimeUtc).TotalMinutes : (double?)null;
            diagnosticsStore.Record(instrument.Symbol, timeframeLabel, scanNow, condition, evaluations,
                new DiagnosticsContext(
                    calendarVeto.State.ToString(), TradingSessions.Describe(scanNow),
                    _cachedCalendar is null ? null : EconomicCalendarVeto.MinutesToNextHighImpact(_cachedCalendar, instrument.AffectedCurrencies, scanNow),
                    freshness, primary?.StrategyId, calendarVeto.State == CalendarVetoState.HardVeto));
            if (primary is null)
            {
                var best = evaluations.OrderByDescending(e => e.Score).FirstOrDefault();
                var reason = best is null
                    ? $"No setup model's precondition is met (Market Condition: {condition})"
                    : $"No model qualified this scan - closest was {best.StrategyId} at {best.Score}/{best.Threshold} (Market Condition: {condition})";
                return new OrchestratorResult(false, null, reason, instrument.Symbol, timeframeLabel, evaluations, lastPrice, completedCandles);
            }

            var candidate = primary.Candidate!;
            var htfAlignment = scanContext.Htf.TryGetValue(candidate.Direction, out var htf) ? htf : (HtfAlignment?)null;
            var tradePlan = await BuildTradePlan(model: primary.StrategyId, mainCandles, displayTimeframe, structure, obs, fvgs, willis, sweeps,
                PlanLevels(keyLevels, scanContext), candidate.Direction, MinStopCostFloor(instrument.Symbol));

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
                lifecycleState, keyLevels, lastPrice, Now(),
                calendarVeto, newsCatalyst,
                scoringProfileId: primary.ScoringProfileId);

            return new OrchestratorResult(true, signal, null, instrument.Symbol, timeframeLabel, evaluations, lastPrice, completedCandles);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Signal orchestration failed for {Symbol} {Timeframe}", instrument.Symbol, timeframeLabel);
            return new OrchestratorResult(false, null, ex.Message, instrument.Symbol, timeframeLabel);
        }
    }

    // A stop must be at least this many times the instrument's round-trip cost
    // (spread plus slippage, from InstrumentMetadataCatalog) so costs cannot eat
    // a large share of the risk. 0 for an instrument with no configured costs -
    // the ATR-based minimum then still applies.
    private const decimal MinStopCostMultiple = 4m;

    // Round-trip spread + slippage + commission of one full position, in price
    // terms (0 for an instrument with no configured costs).
    internal static decimal RoundTripCostPrice(string symbol) =>
        InstrumentMetadataCatalog.TryGet(symbol, out var meta) && meta is not null
            ? (meta.DefaultSpreadUnits + meta.DefaultSlippageUnits + meta.CommissionUnits) * meta.MovementUnitSize
            : 0m;

    internal static decimal MinStopCostFloor(string symbol) => RoundTripCostPrice(symbol) * MinStopCostMultiple;

    // Common safety gates (section 5) that apply to EVERY model and are not part
    // of any model's own weight table.
    internal const int MinIndependentFamilies = 5;
    // Round-trip costs may not exceed a quarter of the risk: with the 4x minimum
    // stop above this can only fail when a stop could not be widened.
    internal const decimal MaxCostFractionOfRisk = 0.25m;

    internal static List<ReasonCode> EvaluateRoutingGates(SetupModelType model, MarketCondition condition, HtfAlignment? htfAlignment)
    {
        var failed = new List<ReasonCode>();
        if (!ModelRouting.Supports(model, condition)) failed.Add(ReasonCode.MARKET_CONDITION_NOT_SUPPORTED);
        if (model == SetupModelType.RangeBoundaryRejection && htfAlignment == HtfAlignment.Conflicting) failed.Add(ReasonCode.HTF_CONFLICT);
        return failed;
    }

    internal static List<ReasonCode> EvaluateCommonGates(int independentFamilies, decimal riskDistance, decimal roundTripCostPrice)
    {
        var failed = new List<ReasonCode>();
        if (independentFamilies < MinIndependentFamilies) failed.Add(ReasonCode.INSUFFICIENT_CONFLUENCE);
        if (riskDistance > 0 && roundTripCostPrice / riskDistance > MaxCostFractionOfRisk) failed.Add(ReasonCode.COST_TOO_HIGH);
        return failed;
    }

    private async Task<TradePlan?> BuildTradePlan(
        SetupModelType model, IReadOnlyList<NormalizedCandle> mainCandles, Timeframe displayTimeframe, StructureResult structure,
        IReadOnlyList<OrderBlock> obs, IReadOnlyList<FairValueGap> fvgs, IReadOnlyList<RealtouchWillisZone> willis,
        IReadOnlyList<LiquiditySweep> sweeps, IReadOnlyList<KeyLevel> keyLevels, SetupDirection direction,
        decimal minStopCostFloor = 0m)
    {
        await Task.CompletedTask;
        // Range Boundary Rejection needs its own plan (target = equilibrium
        // or opposite boundary, section 6.4) - the pullback planner only
        // knows how to plan off an order block/FVG/Willis Zone, which a bare
        // range boundary often doesn't have sitting on it.
        if (model == SetupModelType.RangeBoundaryRejection)
        {
            var lastCloseTime = mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).Last().CloseTimeUtc;
            return RangeTradePlan.Build(mainCandles, displayTimeframe, direction, lastCloseTime, minStopCostFloor);
        }
        return EntryStopTargetCalculator.Compute(mainCandles, displayTimeframe, direction, structure, obs, fvgs, willis, sweeps, keyLevels, minStopCostFloor);
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
        ScanContext scan)
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
        if (!scan.Htf.TryGetValue(direction, out var htfAlignment))
        {
            var evidence = await BuildContextConditions(provider, instrument, providerSymbol, displayTimeframe, condition);
            htfAlignment = TimeframeHierarchy.Evaluate(direction, evidence.Conditions);
            scan.Htf[direction] = htfAlignment;
            if (scan.Levels.Count == 0) scan.Levels.AddRange(evidence.Levels);
            if (scan.Missing.Count == 0) scan.Missing.AddRange(evidence.Missing);
        }

        var tradePlan = await BuildTradePlan(model, mainCandles, displayTimeframe, structure, obs, fvgs, willis, sweeps, PlanLevels(keyLevels, scan), direction, MinStopCostFloor(instrument.Symbol));

        SetupCandidate? pass2 = model switch
        {
            SetupModelType.TrendContinuationPullback => SetupModels.EvaluateTrendContinuationPullback(mainCandles, displayTimeframe, condition, structure, obs, fvgs, willis, sweeps, htfAlignment, tradePlan?.RewardToRisk, calendarVeto.State),
            SetupModelType.BreakoutAndRetest => SetupModels.EvaluateBreakoutAndRetest(mainCandles, displayTimeframe, structure, obs, fvgs, tradePlan?.RewardToRisk, calendarVeto.State, direction),
            SetupModelType.LiquiditySweepReversal => SetupModels.EvaluateLiquiditySweepReversal(mainCandles, displayTimeframe, condition, structure, obs, fvgs, sweeps, htfAlignment, calendarVeto.State, keyLevels),
            SetupModelType.RangeBoundaryRejection => SetupModels.EvaluateRangeBoundaryRejection(mainCandles, displayTimeframe, condition, structure, sweeps, tradePlan?.RewardToRisk, calendarVeto.State, tradePlan?.Targets.Tp2),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };

        var candidate = pass2 ?? pass1;
        var failedGates = candidate.Requirements.Where(r => r.Status == RequirementStatus.NotMet).Select(r => MapReasonCode(r.Description)).ToList();
        if (tradePlan is null) failedGates.Add(ReasonCode.ZONE_NOT_FOUND);
        var warnings = candidate.Requirements.Where(r => r.Status is RequirementStatus.NotEvaluated or RequirementStatus.Unavailable).Select(r => MapReasonCode(r.Description)).ToList();

        var completedForDisplacement = mainCandles.Where(c => c.IsComplete).OrderBy(c => c.OpenTimeUtc).ToList();
        var displacementAtEntry = tradePlan is not null && completedForDisplacement.Count > 0 &&
            Displacement.IsDisplacementCandle(completedForDisplacement, completedForDisplacement.Count - 1);
        // Section 10: how volume is treated is a deterministic function of the asset
        // class and whether reliable volume actually exists in this feed.
        var assetClass = InstrumentMetadataCatalog.TryGet(instrument.Symbol, out var assetMeta) && assetMeta is not null ? assetMeta.AssetClass : "";
        var profile = ScoringProfile.For(assetClass, Displacement.HasUsableVolume(completedForDisplacement));
        var volumeExpansion = profile.ParticipationWeight > 0 && Displacement.HasVolumeExpansion(completedForDisplacement);
        var liquidityEventPresent = sweeps.Any(s => (s.Direction == SweepDirection.Bullish) == (direction == SetupDirection.Long));
        var entryStructureEvent = structure.Events.LastOrDefault(e =>
            direction == SetupDirection.Long ? e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch
                                              : e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch)?.Type;

        ConfluenceScoreResult score = model switch
        {
            SetupModelType.TrendContinuationPullback => ModelScoring.ScoreTrendContinuation(
                htfAlignment, candidate.Requirements.Any(r => r.Description.Contains("discount") && r.Status == RequirementStatus.Met),
                tradePlan?.Entry.ZoneSource, liquidityEventPresent, entryStructureEvent, displacementAtEntry,
                tradePlan?.RewardToRisk, calendarVeto.State, null, profile, volumeExpansion),
            SetupModelType.BreakoutAndRetest => ModelScoring.ScoreBreakoutAndRetest(
                ModelEvidenceBuilder.BuildBreakout(mainCandles, displayTimeframe, structure, obs, fvgs, direction),
                displacementAtEntry, tradePlan?.RewardToRisk, calendarVeto.State, null, profile, volumeExpansion),
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

        // Common safety gates, evaluated once the score (and so the count of
        // independent families) and the plan (and so the risk distance) exist.
        var riskDistance = tradePlan is null ? 0m : Math.Abs(tradePlan.Entry.PreferredEntry - tradePlan.Stop.Price);
        var commonFailures = EvaluateCommonGates(score.Families.Count(f => f.Points > 0), riskDistance, RoundTripCostPrice(instrument.Symbol));
        failedGates.AddRange(commonFailures);

        // Market Condition routing (an unresolved condition trades nothing) and, for
        // range trades, the higher-timeframe conflict gate: buying the floor of a
        // range while the higher timeframes trend down is buying into the trend.
        var routingFailures = EvaluateRoutingGates(model, condition, htfAlignment);
        // Stale or missing data on a required higher timeframe blocks the signal: a
        // decision made with part of the picture missing is not confirmed.
        if (scan.Missing.Count > 0) routingFailures.Add(ReasonCode.DATA_STALE);
        failedGates.AddRange(routingFailures);
        var commonGatesPassed = commonFailures.Count == 0 && routingFailures.Count == 0;
        var gatesPassed = candidate.Qualified && tradePlan is not null && commonGatesPassed;

        return new StrategyEvaluation(model, StrategyVersion, true, gatesPassed, score.TotalScore, score.Grade, ThresholdFor(model),
            score.Families, failedGates, warnings, gatesPassed ? candidate : null, direction, false, profile.Id, tradePlan?.RewardToRisk);
    }

    // Only fully confirmed setups qualify (per-model gates and threshold); the
    // higher score wins.
    internal static StrategyEvaluation? SelectPrimary(IEnumerable<StrategyEvaluation> evaluations, MarketCondition? condition = null) =>
        evaluations.Where(e => e.Qualified)
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => condition.HasValue && ModelRouting.IsNative(e.StrategyId, condition.Value))
            .FirstOrDefault();

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

    private IReadOnlyList<KeyLevel> PlanLevels(IReadOnlyList<KeyLevel> mainLevels, ScanContext scan) =>
        useHigherTimeframeLevels && scan.Levels.Count > 0 ? mainLevels.Concat(scan.Levels).ToList() : mainLevels;

    private async Task<ContextEvidence> BuildContextConditions(
        IMarketDataProvider provider, InstrumentDefinition instrument, string providerSymbol, Timeframe displayTimeframe, MarketCondition mainCondition)
    {
        var contextTfs = TimeframeHierarchy.ContextTimeframes(displayTimeframe).Distinct().ToList();
        var result = new Dictionary<Timeframe, MarketCondition>();
        var levels = new List<KeyLevel>();
        var missing = new List<Timeframe>();
        var now = Now();

        foreach (var tf in contextTfs)
        {
            if (tf == displayTimeframe) { result[tf] = mainCondition; continue; }

            var cacheKey = (provider.Name, instrument.Symbol, tf);
            // A cached entry saved before levels were stored has none: refetch once.
            if (_contextCache.TryGetValue(cacheKey, out var cached) && cached.Levels is not null && now - cached.FetchedAtUtc < ContextCacheTtl(tf))
            {
                result[tf] = cached.Condition;
                levels.AddRange(cached.Levels);
                continue;
            }

            try
            {
                var candles = await provider.GetCandlesAsync(instrument.Symbol, providerSymbol, tf);
                if (!CandleQualityChecker.HasUsableData(candles, 60)) { missing.Add(tf); continue; } // don't fabricate a condition for stale context data
                var structure = StructureAnalyzer.Analyze(candles, tf);
                var condition = MarketConditionClassifier.Classify(candles, tf, structure);
                var tfLevels = KeyLevelCatalog.Build(candles, tf);
                result[tf] = condition;
                levels.AddRange(tfLevels);
                _contextCache[cacheKey] = new CachedCondition(condition, now, tfLevels);
                PersistContextCache();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fetch context timeframe {Tf} for {Symbol}", tf, instrument.Symbol);
                // Fall back to a still-cached (even if expired) value rather than
                // dropping the context entirely on a transient fetch failure.
                if (cached is not null) { result[tf] = cached.Condition; if (cached.Levels is not null) levels.AddRange(cached.Levels); }
                else missing.Add(tf);
            }
        }
        return new ContextEvidence(result, levels, missing);
    }

    private void PersistContextCache() =>
        DiskCache.Save(_contextCachePath, _contextCache.Select(kv =>
            new ContextCacheEntry(kv.Key.Provider, kv.Key.Symbol, kv.Key.Tf, kv.Value.Condition, kv.Value.FetchedAtUtc, kv.Value.Levels)).ToList());

    private async Task<CalendarVetoResult> GetCalendarVeto(IReadOnlyCollection<string> affectedCurrencies)
    {
        var now = Now();
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
        var now = Now();
        if (!_newsCache.TryGetValue(canonicalSymbol, out var cached) || now - cached.FetchedAtUtc >= NewsCacheTtl)
        {
            var result = await newsProvider.GetNewsAsync(canonicalSymbol);
            cached = (result, now);
            _newsCache[canonicalSymbol] = cached;
            DiskCache.Save(_newsCachePath, _newsCache.ToDictionary(kv => kv.Key, kv => new NewsCacheEntry(kv.Value.Result, kv.Value.FetchedAtUtc)));
        }
        return NewsCatalystEvaluator.Evaluate(cached.Result, direction, now);
    }
}
