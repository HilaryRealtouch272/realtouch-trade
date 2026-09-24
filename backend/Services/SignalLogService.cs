using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// Section 17's per-trade record. The original 19 fields stay first, in
// their original order, so every existing named-argument call site (and
// every persisted historical row) keeps working unchanged - all new fields
// are appended at the end with defaults, never inserted into the middle.
public record QualificationLogEntry(
    string Id, string Symbol, string Timeframe, string Direction, string SetupModel, string Grade, int Score,
    decimal Entry, decimal Stop, decimal Tp1, decimal Tp2, decimal Tp3, decimal RewardToRisk,
    // TrackingExpiryUtc is when THIS ledger entry auto-closes as "Expired"
    // if nothing else has resolved it first - QualifiedAtUtc + a holding
    // window scaled to the timeframe, computed once at creation and stored
    // so the displayed value is stable (not recomputed differently later).
    DateTime QualifiedAtUtc, DateTime TrackingExpiryUtc,
    // Open -> Tp1Hit -> Tp2Hit -> (Tp3Hit | StoppedOut | Expired). The last
    // three are terminal - RealizedR and ClosedAtUtc are only set then.
    string Status,
    DateTime? Tp1HitAtUtc, DateTime? Tp2HitAtUtc, DateTime? ClosedAtUtc, decimal? RealizedR,
    // --- Section 17 extension: everything below is set once at creation
    // (StrategyVersion..SlippageUnits), updated on every live price check
    // (MaxFavorableExcursionR/MaxAdverseExcursionR), or computed once at
    // closure (everything from Tp3HitAtUtc down) - never recomputed
    // differently later, same "computed once, stored stable" rule as
    // TrackingExpiryUtc above.
    string StrategyVersion = "v1-strategy-engine",
    string AssetClass = "",
    string MarketCondition = "",
    decimal FinalStop = 0m, // no breakeven-stop adjustment exists yet - always equals Stop for now, field kept for when it does
    decimal RiskPercent = 0m,
    decimal RiskAmount = 0m,
    decimal PositionSize = 0m,
    decimal EntrySpreadUnits = 0m,
    decimal SlippageUnits = 0m,
    DateTime? Tp3HitAtUtc = null,
    DateTime? StopHitAtUtc = null,
    // Running excursion in R-multiples, updated on every live price check
    // regardless of whether the trade closes this call - MFE/MAE capture
    // the BEST and WORST the trade ever did, which the final Status alone
    // cannot: a trade that ran to +3R before eventually stopping out at
    // -1R looks identical to one that never moved, if all you store is the
    // final R.
    decimal? MaxFavorableExcursionR = null,
    decimal? MaxAdverseExcursionR = null,
    decimal? GrossMovementUnits = null,
    decimal? CostMovementUnits = null,
    decimal? NetMovementUnits = null,
    string? MovementUnitLabel = null,
    decimal? MonetaryPnL = null,
    decimal? PercentageReturn = null,
    string? FinalOutcome = null,
    string? ClosureReason = null,
    double? HoldingDurationHours = null,
    // Non-null marks a trade that entered through the universal score floor
    // (unconfirmed gates and/or under its model's own threshold). Kept on
    // the row so those trades can be measured separately from fully
    // confirmed ones - mixing the two would blur what each is worth.
    string? ScoreFloorNote = null,
    // --- Fill integrity (sections 14 to 17). NetRealizedR is RealizedR minus the
    // round-trip costs expressed in R; monetary P&L and return use NET R.
    decimal? NetRealizedR = null,
    // Every executed slice of the position, with its simulated fill and costs.
    // Null only on rows persisted before exits were recorded.
    IReadOnlyList<ExitFill>? Exits = null,
    // True when a stop and a target both sat inside one candle and the order
    // could not be resolved from finer candles: the conservative (stop-first)
    // outcome stands and is labelled uncertain, never favourable.
    bool IntrabarSequenceUncertain = false,
    decimal CommissionUnits = 0m,
    decimal? EntryFillPrice = null,
    string? EntryType = null,
    DateTime? EntryTimeUtc = null,
    // Which scoring profile produced this signal's score (section 10).
    string? ScoringProfileId = null,
    // The close time of the last market candle already applied to this trade.
    // Outcome processing resumes from here after a restart or a missed scan, so
    // no interval is skipped and none is applied twice.
    DateTime? LastProcessedUtc = null,
    // Context at the moment the trade qualified (report: persist session, news risk
    // and calendar state on every candidate and resolved trade).
    string? Session = null,
    string? CalendarState = null,
    string? NewsState = null
);

// A permanent ledger of every real qualification (grade B or better) the
// engine has found, with honest outcome tracking against later live prices -
// separate from SignalAlertService's Telegram-dedup state, which only cares
// about not re-notifying, not about keeping history. One open entry per
// (symbol, timeframe) at a time: a setup that stays qualified scan-to-scan is
// the SAME tracked trade, not a new row each time.
//
// Honest limitation: performance can only update on a scan where that
// (symbol, timeframe) still returns a successful result with a live price -
// if it later fails to evaluate (stale data, no candidate at all), that
// entry simply doesn't move until a future successful scan resumes it.
public class SignalLogService(TelegramNotifier telegram, IHostEnvironment env, ILogger<SignalLogService> logger, IFineCandleSource? fineSource = null)
{
    private const int MaxEntries = 1000;

    private readonly string _path = Path.Combine(env.ContentRootPath, ".cache", "signal-log.json");
    private readonly List<QualificationLogEntry> _entries = LoadAndMigrate(Path.Combine(env.ContentRootPath, ".cache", "signal-log.json"));
    private readonly Dictionary<string, string> _openKeyToEntryId = InitOpenKeys(
        LoadAndMigrate(Path.Combine(env.ContentRootPath, ".cache", "signal-log.json")));
    private readonly object _lock = new();

    // Ledger entries persisted before TrackingExpiryUtc existed deserialize
    // it as default(DateTime) (0001-01-01) - which is always in the past, so
    // IsOpenStatus + now > TrackingExpiryUtc fired instantly and mass-expired
    // every pre-existing open entry the moment this shipped. Backfill it the
    // same way it's computed on creation so old rows get a real expiry
    // instead of silently detonating on the next scan.
    private static List<QualificationLogEntry> LoadAndMigrate(string path)
    {
        var entries = DiskCache.Load<List<QualificationLogEntry>>(path) ?? new();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.TrackingExpiryUtc == default)
                entries[i] = e with { TrackingExpiryUtc = e.QualifiedAtUtc + HoldingWindow(e.Timeframe) };
        }
        return entries;
    }

    private static Dictionary<string, string> InitOpenKeys(List<QualificationLogEntry> entries)
    {
        var map = new Dictionary<string, string>();
        foreach (var e in entries.Where(e => IsOpenStatus(e.Status)))
            map[Key(e.Symbol, e.Timeframe)] = e.Id;
        return map;
    }

    private static string Key(string symbol, string timeframe) => $"{symbol}|{timeframe}";
    private static bool IsOpenStatus(string status) => status is "Open" or "Tp1Hit" or "Tp2Hit";

    // Fire-and-forget from the caller's point of view (same reasoning as
    // SignalAlertService): a Telegram outage must never slow down or fail
    // the scan itself, so outcome notifications happen after the ledger is
    // already updated and persisted, and any send failure is only logged.
    public async Task RecordAndTrackAsync(IEnumerable<OrchestratorResult> results)
    {
        var resultList = results.ToList();
        var changed = new List<QualificationLogEntry>();
        // The 15m candles fetched this scan are the finest available: they let a
        // coarser trade settle which of a stop and a target came first inside
        // one candle, instead of falling back to the conservative guess.
        var fineByEntry = await FetchFineCandlesForOpenTradesAsync();
        var fineBySymbol = resultList
            .Where(r => r.Timeframe == "15m" && r.Candles is { Count: > 0 })
            .GroupBy(r => r.InstrumentSymbol)
            .ToDictionary(g => g.Key, g => g.First().Candles!);
        lock (_lock)
        {
            foreach (var result in resultList)
            {
                var updated = UpdatePerformanceLocked(result, fineBySymbol.GetValueOrDefault(result.InstrumentSymbol), fineByEntry);
                if (updated is not null) changed.Add(updated);
            }

            // A 15m result is fresh for EVERY open trade on that symbol, not
            // just ones qualified on 15m itself - the M15 cadence (checked
            // every ~10min, effectively every scan run) is far faster than
            // 1H/4H/Daily/Weekly's own (20min/45min/2h/6h), so a Weekly-
            // logged trade's stop/TP would otherwise only get re-checked
            // once every 6 hours. Entry/stop/targets are fixed at
            // qualification time; only the live price needs to be current,
            // and market price doesn't care which timeframe's chart you're
            // looking at it on. Deliberately one-directional: this never
            // runs the other way (a stale Daily/Weekly close checking a
            // fast trade), since that price could be hours to days old.
            foreach (var result in resultList.Where(r => r.Timeframe == "15m" && (r.Candles is { Count: > 0 } || r.LivePrice.HasValue)))
                changed.AddRange(PropagateFastPriceToOtherTimeframesLocked(result.InstrumentSymbol, result.Candles, result.LivePrice, result.Timeframe, fineByEntry));

            foreach (var result in resultList) RecordIfNewLocked(result);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        Persist();

        foreach (var entry in changed)
        {
            var (success, error) = await telegram.SendAsync(FormatOutcomeMessage(entry));
            if (!success) logger.LogWarning("Outcome Telegram alert failed for {Symbol} {Timeframe} ({Status}): {Error}", entry.Symbol, entry.Timeframe, entry.Status, error);
        }
    }

    // One-minute candles for every open trade whose market has a fine feed,
    // fetched from each trade's watermark. A failed fetch just leaves that trade
    // on the coarser candle path this scan - it is never fatal.
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<Models.NormalizedCandle>>> FetchFineCandlesForOpenTradesAsync()
    {
        var map = new Dictionary<string, IReadOnlyList<Models.NormalizedCandle>>();
        if (fineSource is null) return map;

        List<QualificationLogEntry> open;
        lock (_lock) open = _entries.Where(e => IsOpenStatus(e.Status)).ToList();

        foreach (var e in open)
        {
            try
            {
                var candles = await fineSource.GetOneMinuteAsync(e.Symbol, e.LastProcessedUtc ?? e.QualifiedAtUtc);
                if (candles.Count > 0) map[e.Id] = candles;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "One-minute candles unavailable for {Symbol}; falling back to coarser candles this scan", e.Symbol);
            }
        }
        return map;
    }

    private static string? FirstWord(string? state) => string.IsNullOrWhiteSpace(state) ? null : state.Split(new[] { ':', ' ' }, 2)[0];

    private void RecordIfNewLocked(OrchestratorResult result)
    {
        if (!result.Success || result.Signal is null) return;
        var signal = result.Signal;
        if (signal.Grade is not ("A+" or "A" or "B")) return;
        // A real, meeting-the-bar setup that price hasn't actually traded
        // into yet is a genuine setup - just not a filled position. Logging
        // it as "Open" immediately meant the very first price check could
        // find current price already past TP1/TP2 (since those targets sit
        // below - or above, for a Long - the entry zone, and price never
        // needed to enter that zone to already be past them), reporting a
        // "TP2 hit" trade that was never actually entered. Wait for
        // EntryPlan.Triggered (price genuinely in the zone + a real
        // matching-direction structure event) before this becomes a tracked,
        // P&L-bearing position.
        if (!signal.Triggered) return;

        var key = Key(result.InstrumentSymbol, result.Timeframe);
        if (_openKeyToEntryId.ContainsKey(key)) return; // already tracking an open trade here
        if (IsInCooldownLocked(result.InstrumentSymbol, result.Timeframe, signal.Direction.ToString())) return;

        var qualifiedAt = DateTime.UtcNow;
        // Real per-instrument metadata when configured; an unconfigured
        // symbol still gets tracked (never silently drop a real qualifying
        // trade over missing pip-display config) but with empty asset
        // class/zeroed cost fields rather than a guessed unit size.
        InstrumentMetadataCatalog.TryGet(result.InstrumentSymbol, out var meta);
        var entry = new QualificationLogEntry(
            Id: Guid.NewGuid().ToString("N"),
            Symbol: result.InstrumentSymbol, Timeframe: result.Timeframe,
            Direction: signal.Direction.ToString(), SetupModel: signal.SetupModel.ToString(),
            Grade: signal.Grade, Score: signal.SetupQualityScore,
            Entry: signal.PreferredEntry, Stop: signal.Stop, Tp1: signal.Tp1, Tp2: signal.Tp2, Tp3: signal.Tp3,
            RewardToRisk: signal.RewardToRisk,
            QualifiedAtUtc: qualifiedAt, TrackingExpiryUtc: qualifiedAt + HoldingWindow(result.Timeframe),
            Status: "Open", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null,
            StrategyVersion: signal.StrategyVersion, AssetClass: meta?.AssetClass ?? "", MarketCondition: signal.Condition.ToString(),
            FinalStop: signal.Stop, RiskPercent: signal.RiskPercent, RiskAmount: signal.RiskAmount, PositionSize: signal.PositionSize,
            EntrySpreadUnits: meta?.DefaultSpreadUnits ?? 0m, SlippageUnits: meta?.DefaultSlippageUnits ?? 0m,
            ScoreFloorNote: signal.ScoreFloorNote,
            Exits: Array.Empty<ExitFill>(), CommissionUnits: meta?.CommissionUnits ?? 0m,
            EntryFillPrice: signal.PreferredEntry, EntryType: TradeSimulator.EntryTypeDescription, EntryTimeUtc: qualifiedAt,
            ScoringProfileId: signal.ScoringProfileId,
            Session: TradingSessions.Describe(qualifiedAt),
            CalendarState: FirstWord(signal.EconomicCalendarState), NewsState: FirstWord(signal.NewsState));

        _entries.Add(entry);
        _openKeyToEntryId[key] = entry.Id;
    }

    // Returns the updated entry only when its Status actually changed this
    // call (a real, notification-worthy event), null otherwise.
    private QualificationLogEntry? UpdatePerformanceLocked(OrchestratorResult result, IReadOnlyList<Models.NormalizedCandle>? fineCandles = null,
        IReadOnlyDictionary<string, IReadOnlyList<Models.NormalizedCandle>>? fineByEntry = null)
    {
        var key = Key(result.InstrumentSymbol, result.Timeframe);
        if (!_openKeyToEntryId.TryGetValue(key, out var entryId)) return null;

        var index = _entries.FindIndex(e => e.Id == entryId);
        if (index < 0) { _openKeyToEntryId.Remove(key); return null; }

        var entry = _entries[index];
        var statusBefore = entry.Status;
        // The real candle history for this scan, regardless of whether a NEW
        // candidate qualified - an open trade's stop/targets must be
        // checked on every scan it's still tracked, not only on scans where
        // a fresh setup happens to qualify (see OrchestratorResult.LivePrice
        // and .Candles). Prefer the full candle sequence (catches a stop hit
        // and later reversal within the same scan gap); fall back to the
        // single scalar price when no candle list was supplied.
        entry = fineByEntry is not null && fineByEntry.TryGetValue(entry.Id, out var oneMinute)
            ? TradeSimulator.ApplyFineSequence(entry, oneMinute, DateTime.UtcNow)
            : result.Candles is { Count: > 0 }
                ? ApplyCandleSequence(entry, result.Candles, DateTime.UtcNow, result.Timeframe == "15m" ? null : fineCandles)
                : ApplyPriceAndExpiry(entry, result.LivePrice, DateTime.UtcNow);

        _entries[index] = entry;
        if (!IsOpenStatus(entry.Status)) _openKeyToEntryId.Remove(key);
        return entry.Status != statusBefore ? entry : null;
    }

    // Checks a fresh price against every OTHER open entry for the same
    // symbol (any timeframe but the one this price already came from -
    // that one was just handled by UpdatePerformanceLocked above).
    private List<QualificationLogEntry> PropagateFastPriceToOtherTimeframesLocked(string symbol, IReadOnlyList<Models.NormalizedCandle>? candles, decimal? livePrice, string sourceTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<Models.NormalizedCandle>>? fineByEntry = null)
    {
        var changed = new List<QualificationLogEntry>();
        var now = DateTime.UtcNow;
        var sourceKey = Key(symbol, sourceTimeframe);
        var keys = _openKeyToEntryId.Keys.Where(k => k != sourceKey && k.StartsWith(symbol + "|", StringComparison.Ordinal)).ToList();

        foreach (var key in keys)
        {
            var entryId = _openKeyToEntryId[key];
            var index = _entries.FindIndex(e => e.Id == entryId);
            if (index < 0) { _openKeyToEntryId.Remove(key); continue; }

            var entry = _entries[index];
            var statusBefore = entry.Status;
            entry = fineByEntry is not null && fineByEntry.TryGetValue(entry.Id, out var oneMinute)
                ? TradeSimulator.ApplyFineSequence(entry, oneMinute, now)
                : candles is { Count: > 0 } ? ApplyCandleSequence(entry, candles, now) : ApplyPriceAndExpiry(entry, livePrice, now);

            _entries[index] = entry;
            if (!IsOpenStatus(entry.Status)) _openKeyToEntryId.Remove(key);
            if (entry.Status != statusBefore) changed.Add(entry);
        }
        return changed;
    }

    // The simulation itself (fills, costs, partial exits, gaps, same-candle
    // sequencing) lives in TradeSimulator so the backtester runs the SAME
    // logic as the live ledger. These forwarders keep existing callers stable.
    internal static QualificationLogEntry ApplyPriceAndExpiry(QualificationLogEntry entry, decimal? livePrice, DateTime now) =>
        TradeSimulator.ApplyPriceAndExpiry(entry, livePrice, now);

    internal static QualificationLogEntry ApplyCandleSequence(QualificationLogEntry entry, IReadOnlyList<Models.NormalizedCandle> candles, DateTime now,
        IReadOnlyList<Models.NormalizedCandle>? fineCandles = null) =>
        TradeSimulator.ApplyCandleSequence(entry, candles, now, fineCandles);

    private static string FormatOutcomeMessage(QualificationLogEntry entry)
    {
        var (icon, label) = entry.Status switch
        {
            "Tp1Hit" => ("🎯", "TP1 hit"),
            "Tp2Hit" => ("🎯", "TP2 hit"),
            "Tp3Hit" => ("🏆", "TP3 hit — WIN"),
            "StoppedOut" => ("🛑", "Stopped out — LOSS"),
            "Expired" => ("⌛", "Expired — flat"),
            _ => ("ℹ️", entry.Status)
        };
        var directionIcon = entry.Direction == "Long" ? "🟢" : "🔴";
        var realizedR = entry.NetRealizedR ?? entry.RealizedR;
        var realized = realizedR.HasValue
            ? $"\n⚖️ Realized: {realizedR.Value:0.00}R" + (entry.NetRealizedR.HasValue ? " net of costs" : "") + (entry.IntrabarSequenceUncertain ? " (intrabar sequence uncertain)" : "")
            : "";

        // Show whichever level actually drove this outcome - the stop only
        // for a real stop-out, the specific TP that was hit for a TP event.
        // Expired never actually reached either one (it just ran out of
        // tracking time), so its stop is shown too but explicitly labeled
        // "not reached" rather than looking like a real stop-out.
        var levelLine = entry.Status switch
        {
            "Tp1Hit" => $"TP1 {TelegramSignalFormatter.FormatPrice(entry.Tp1)} · ",
            "Tp2Hit" => $"TP2 {TelegramSignalFormatter.FormatPrice(entry.Tp2)} · ",
            "Tp3Hit" => $"TP3 {TelegramSignalFormatter.FormatPrice(entry.Tp3)} · ",
            "StoppedOut" => $"Stop {TelegramSignalFormatter.FormatPrice(entry.Stop)} · ",
            "Expired" => $"Stop {TelegramSignalFormatter.FormatPrice(entry.Stop)} (not reached) · ",
            _ => ""
        };

        return
            $"{icon} *{entry.Symbol}* · {entry.Timeframe} · {directionIcon} {entry.Direction.ToUpperInvariant()} — *{label}*\n" +
            $"Original setup: Entry {TelegramSignalFormatter.FormatPrice(entry.Entry)} · {levelLine}Grade {entry.Grade} ({entry.Score}/100){realized}";
    }

    // A real, generous holding window scaled to the timeframe's own candle
    // duration, measured from when THIS ledger entry started tracking - NOT
    // the underlying signal's ExpiryUtc (a zone-planning concept from
    // EntryStopTargetCalculator: how long to wait for a zone to trigger,
    // measured from when the ZONE formed). That field can already be in the
    // past the moment a still-valid, still-qualifying setup is first logged
    // here (e.g. an old but unmitigated order block) - using it for the
    // ledger's own lifecycle was a real bug: a genuinely still-qualifying
    // setup was marked "Expired" within minutes, then immediately re-logged
    // as brand new on the very next scan, over and over.
    private static TimeSpan HoldingWindow(string timeframeLabel) =>
        TimeframeConfig.Duration(TimeframeIntervals.ParseLabel(timeframeLabel) ?? Timeframe.H1) * 30;

    // A copy of the ledger for read-only analysis (reconciliation).
    public IReadOnlyList<QualificationLogEntry> GetAll()
    {
        lock (_lock) return _entries.OrderByDescending(e => e.QualifiedAtUtc).ToList();
    }

    // Used by SignalAlertService to keep alerts consistent with what the
    // ledger actually tracks: one open trade per (symbol, timeframe)
    // regardless of direction (see RecordIfNewLocked) - an opposite-
    // direction reversal while the original is still open never gets a
    // ledger row, so it shouldn't get a fresh Telegram alert either. Returns
    // the currently open entry's direction, or null if nothing is open.
    // After a trade closes, the same idea in the same direction may not be
    // re-entered for a few candles: a setup that just failed is still "there" on
    // the very next scan, and re-firing it immediately is re-buying the loss.
    internal const int CooldownCandles = 4;

    private bool IsInCooldownLocked(string symbol, string timeframe, string direction)
    {
        var window = TimeframeConfig.Duration(TimeframeIntervals.ParseLabel(timeframe) ?? Timeframe.H1) * CooldownCandles;
        var now = DateTime.UtcNow;
        return _entries.Any(e => e.Symbol == symbol && e.Timeframe == timeframe && e.Direction == direction
            && e.ClosedAtUtc is { } closed && now - closed < window);
    }

    // True when a signal would be blocked by the post-close cooldown; used by the
    // alert service so Telegram never announces something the ledger refuses.
    public bool IsInCooldown(string symbol, string timeframe, string direction)
    {
        lock (_lock) return IsInCooldownLocked(symbol, timeframe, direction);
    }

    public string? GetOpenDirection(string symbol, string timeframe)
    {
        lock (_lock)
        {
            if (!_openKeyToEntryId.TryGetValue(Key(symbol, timeframe), out var entryId)) return null;
            return _entries.FirstOrDefault(e => e.Id == entryId)?.Direction;
        }
    }

    // Real, deliberate user actions on real money-relevant records - deletion
    // is permanent (no undo), which is exactly why the frontend gates both
    // behind an explicit confirmation dialog before ever calling these.
    public bool DeleteEntry(string id)
    {
        bool removed;
        lock (_lock)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0) return false;
            var entry = _entries[index];
            _entries.RemoveAt(index);
            var key = Key(entry.Symbol, entry.Timeframe);
            if (_openKeyToEntryId.TryGetValue(key, out var openId) && openId == id)
                _openKeyToEntryId.Remove(key);
            removed = true;
        }
        if (removed) Persist();
        return removed;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _entries.Clear();
            _openKeyToEntryId.Clear();
        }
        Persist();
    }

    private void Persist()
    {
        List<QualificationLogEntry> snapshot;
        lock (_lock) snapshot = new List<QualificationLogEntry>(_entries);
        DiskCache.Save(_path, snapshot);
    }
}
