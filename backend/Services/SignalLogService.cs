using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

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
    DateTime? Tp1HitAtUtc, DateTime? Tp2HitAtUtc, DateTime? ClosedAtUtc, decimal? RealizedR
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
public class SignalLogService(TelegramNotifier telegram, IHostEnvironment env, ILogger<SignalLogService> logger)
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
        lock (_lock)
        {
            foreach (var result in resultList)
            {
                var updated = UpdatePerformanceLocked(result);
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
            foreach (var result in resultList.Where(r => r.Timeframe == "15m" && r.Success && r.Signal is not null))
                changed.AddRange(PropagateFastPriceToOtherTimeframesLocked(result.InstrumentSymbol, result.Signal!.LivePrice, result.Timeframe));

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

    private void RecordIfNewLocked(OrchestratorResult result)
    {
        if (!result.Success || result.Signal is null) return;
        var signal = result.Signal;
        if (signal.Grade is not ("A+" or "A" or "B")) return;

        var key = Key(result.InstrumentSymbol, result.Timeframe);
        if (_openKeyToEntryId.ContainsKey(key)) return; // already tracking an open trade here

        var qualifiedAt = DateTime.UtcNow;
        var entry = new QualificationLogEntry(
            Id: Guid.NewGuid().ToString("N"),
            Symbol: result.InstrumentSymbol, Timeframe: result.Timeframe,
            Direction: signal.Direction.ToString(), SetupModel: signal.SetupModel.ToString(),
            Grade: signal.Grade, Score: signal.SetupQualityScore,
            Entry: signal.PreferredEntry, Stop: signal.Stop, Tp1: signal.Tp1, Tp2: signal.Tp2, Tp3: signal.Tp3,
            RewardToRisk: signal.RewardToRisk,
            QualifiedAtUtc: qualifiedAt, TrackingExpiryUtc: qualifiedAt + HoldingWindow(result.Timeframe),
            Status: "Open", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null);

        _entries.Add(entry);
        _openKeyToEntryId[key] = entry.Id;
    }

    // Returns the updated entry only when its Status actually changed this
    // call (a real, notification-worthy event), null otherwise.
    private QualificationLogEntry? UpdatePerformanceLocked(OrchestratorResult result)
    {
        var key = Key(result.InstrumentSymbol, result.Timeframe);
        if (!_openKeyToEntryId.TryGetValue(key, out var entryId)) return null;

        var index = _entries.FindIndex(e => e.Id == entryId);
        if (index < 0) { _openKeyToEntryId.Remove(key); return null; }

        var entry = _entries[index];
        var statusBefore = entry.Status;
        var livePrice = result.Success && result.Signal is not null ? result.Signal.LivePrice : (decimal?)null;
        entry = ApplyPriceAndExpiry(entry, livePrice, DateTime.UtcNow);

        _entries[index] = entry;
        if (!IsOpenStatus(entry.Status)) _openKeyToEntryId.Remove(key);
        return entry.Status != statusBefore ? entry : null;
    }

    // Checks a fresh price against every OTHER open entry for the same
    // symbol (any timeframe but the one this price already came from -
    // that one was just handled by UpdatePerformanceLocked above).
    private List<QualificationLogEntry> PropagateFastPriceToOtherTimeframesLocked(string symbol, decimal livePrice, string sourceTimeframe)
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
            entry = ApplyPriceAndExpiry(entry, livePrice, now);

            _entries[index] = entry;
            if (!IsOpenStatus(entry.Status)) _openKeyToEntryId.Remove(key);
            if (entry.Status != statusBefore) changed.Add(entry);
        }
        return changed;
    }

    // Shared by both the exact-timeframe update and the fast-price
    // propagation above: applies a live price (when one is available) then
    // the time-based expiry check, honestly - never invents a status change
    // without either real price movement or real elapsed time behind it.
    internal static QualificationLogEntry ApplyPriceAndExpiry(QualificationLogEntry entry, decimal? livePrice, DateTime now)
    {
        if (livePrice.HasValue)
        {
            var price = livePrice.Value;
            var isLong = entry.Direction == "Long";
            var riskDistance = Math.Abs(entry.Entry - entry.Stop);

            bool Reached(decimal level) => isLong ? price >= level : price <= level;
            bool StoppedOut() => isLong ? price <= entry.Stop : price >= entry.Stop;

            if (StoppedOut())
            {
                // The strategy's own target plan scales out 25%/50%/25% of the
                // position at TP1/TP2/TP3 (EntryStopTargetCalculator's fixed
                // weights) - a trade that already banked TP1 and/or TP2 before
                // the REMAINING runner hit the original stop is a net win or a
                // smaller loss, not the flat -1R a full-position stop implies.
                // This model has no breakeven-stop adjustment, so the unclosed
                // remainder is honestly assumed to take the full stop loss.
                var r = BlendedRealizedR(entry, riskDistance, remainingOutcomeR: -1m);
                entry = entry with { Status = "StoppedOut", ClosedAtUtc = now, RealizedR = r };
            }
            else if (Reached(entry.Tp3))
            {
                // Price physically traversed TP1 and TP2 to reach TP3 even if a
                // scan gap meant they were never separately recorded - the full
                // three-way blend is the honest outcome here, not just the R at TP3.
                var r1 = riskDistance == 0 ? 0 : Math.Abs(entry.Tp1 - entry.Entry) / riskDistance;
                var r2 = riskDistance == 0 ? 0 : Math.Abs(entry.Tp2 - entry.Entry) / riskDistance;
                var r3 = riskDistance == 0 ? 0 : Math.Abs(entry.Tp3 - entry.Entry) / riskDistance;
                var r = EntryStopTargetCalculator.Tp1Weight * r1 + EntryStopTargetCalculator.Tp2Weight * r2 + EntryStopTargetCalculator.Tp3Weight * r3;
                entry = entry with { Status = "Tp3Hit", ClosedAtUtc = now, RealizedR = r };
            }
            else if (Reached(entry.Tp2) && entry.Status != "Tp2Hit")
            {
                entry = entry with { Status = "Tp2Hit", Tp2HitAtUtc = entry.Tp2HitAtUtc ?? now };
            }
            else if (Reached(entry.Tp1) && entry.Status == "Open")
            {
                entry = entry with { Status = "Tp1Hit", Tp1HitAtUtc = entry.Tp1HitAtUtc ?? now };
            }
        }

        if (IsOpenStatus(entry.Status) && now > entry.TrackingExpiryUtc)
        {
            // Whatever wasn't closed by a real target hit just times out with
            // no further information - honestly treated as flat (0R) on that
            // remaining slice, not assumed to have kept moving favorably.
            var riskDistance = Math.Abs(entry.Entry - entry.Stop);
            var r = BlendedRealizedR(entry, riskDistance, remainingOutcomeR: 0m);
            entry = entry with { Status = "Expired", ClosedAtUtc = now, RealizedR = r };
        }

        return entry;
    }

    // Blends whatever TP1/TP2 profit has already been banked (per the
    // position's own real Tp1HitAtUtc/Tp2HitAtUtc record, not just its
    // current Status) with the outcome applied to whatever weight remains
    // unclosed - the same weights EntryStopTargetCalculator actually plans
    // the position around, not an assumption that only the final event matters.
    internal static decimal BlendedRealizedR(QualificationLogEntry entry, decimal riskDistance, decimal remainingOutcomeR)
    {
        decimal Multiple(decimal level) => riskDistance == 0 ? 0 : Math.Abs(level - entry.Entry) / riskDistance;

        var banked = 0m;
        var remainingWeight = 1m;
        if (entry.Tp1HitAtUtc is not null)
        {
            banked += EntryStopTargetCalculator.Tp1Weight * Multiple(entry.Tp1);
            remainingWeight -= EntryStopTargetCalculator.Tp1Weight;
        }
        if (entry.Tp2HitAtUtc is not null)
        {
            banked += EntryStopTargetCalculator.Tp2Weight * Multiple(entry.Tp2);
            remainingWeight -= EntryStopTargetCalculator.Tp2Weight;
        }
        return banked + remainingWeight * remainingOutcomeR;
    }

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
        var realized = entry.RealizedR.HasValue ? $"\n⚖️ Realized: {entry.RealizedR.Value:0.00}R" : "";

        return
            $"{icon} *{entry.Symbol}* · {entry.Timeframe} · {directionIcon} {entry.Direction.ToUpperInvariant()} — *{label}*\n" +
            $"Original setup: Entry {TelegramSignalFormatter.FormatPrice(entry.Entry)} · Stop {TelegramSignalFormatter.FormatPrice(entry.Stop)} · Grade {entry.Grade} ({entry.Score}/100){realized}";
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

    public IReadOnlyList<QualificationLogEntry> GetAll()
    {
        lock (_lock) return _entries.OrderByDescending(e => e.QualifiedAtUtc).ToList();
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
