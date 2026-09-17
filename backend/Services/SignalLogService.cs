using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Services;

public record QualificationLogEntry(
    string Id, string Symbol, string Timeframe, string Direction, string SetupModel, string Grade, int Score,
    decimal Entry, decimal Stop, decimal Tp1, decimal Tp2, decimal Tp3, decimal RewardToRisk,
    DateTime QualifiedAtUtc, DateTime ExpiryUtc,
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
public class SignalLogService
{
    private const int MaxEntries = 1000;

    private readonly string _path;
    private readonly List<QualificationLogEntry> _entries;
    private readonly Dictionary<string, string> _openKeyToEntryId = new();
    private readonly object _lock = new();

    public SignalLogService(IHostEnvironment env)
    {
        _path = Path.Combine(env.ContentRootPath, ".cache", "signal-log.json");
        _entries = DiskCache.Load<List<QualificationLogEntry>>(_path) ?? new();
        foreach (var e in _entries.Where(e => IsOpenStatus(e.Status)))
            _openKeyToEntryId[Key(e.Symbol, e.Timeframe)] = e.Id;
    }

    private static string Key(string symbol, string timeframe) => $"{symbol}|{timeframe}";
    private static bool IsOpenStatus(string status) => status is "Open" or "Tp1Hit" or "Tp2Hit";

    public void RecordAndTrack(IEnumerable<OrchestratorResult> results)
    {
        var resultList = results.ToList();
        lock (_lock)
        {
            foreach (var result in resultList) UpdatePerformanceLocked(result);
            foreach (var result in resultList) RecordIfNewLocked(result);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        Persist();
    }

    private void RecordIfNewLocked(OrchestratorResult result)
    {
        if (!result.Success || result.Signal is null) return;
        var signal = result.Signal;
        if (signal.Grade is not ("A+" or "A" or "B")) return;

        var key = Key(result.InstrumentSymbol, result.Timeframe);
        if (_openKeyToEntryId.ContainsKey(key)) return; // already tracking an open trade here

        var entry = new QualificationLogEntry(
            Id: Guid.NewGuid().ToString("N"),
            Symbol: result.InstrumentSymbol, Timeframe: result.Timeframe,
            Direction: signal.Direction.ToString(), SetupModel: signal.SetupModel.ToString(),
            Grade: signal.Grade, Score: signal.SetupQualityScore,
            Entry: signal.PreferredEntry, Stop: signal.Stop, Tp1: signal.Tp1, Tp2: signal.Tp2, Tp3: signal.Tp3,
            RewardToRisk: signal.RewardToRisk,
            QualifiedAtUtc: DateTime.UtcNow, ExpiryUtc: signal.ExpiryUtc,
            Status: "Open", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null);

        _entries.Add(entry);
        _openKeyToEntryId[key] = entry.Id;
    }

    private void UpdatePerformanceLocked(OrchestratorResult result)
    {
        var key = Key(result.InstrumentSymbol, result.Timeframe);
        if (!_openKeyToEntryId.TryGetValue(key, out var entryId)) return;

        var index = _entries.FindIndex(e => e.Id == entryId);
        if (index < 0) { _openKeyToEntryId.Remove(key); return; }
        var entry = _entries[index];
        var now = DateTime.UtcNow;

        if (result.Success && result.Signal is not null)
        {
            var price = result.Signal.LivePrice;
            var isLong = entry.Direction == "Long";
            var riskDistance = Math.Abs(entry.Entry - entry.Stop);

            bool Reached(decimal level) => isLong ? price >= level : price <= level;
            bool StoppedOut() => isLong ? price <= entry.Stop : price >= entry.Stop;

            if (StoppedOut())
            {
                entry = entry with { Status = "StoppedOut", ClosedAtUtc = now, RealizedR = -1m };
            }
            else if (Reached(entry.Tp3))
            {
                var r = riskDistance == 0 ? 0 : Math.Abs(entry.Tp3 - entry.Entry) / riskDistance;
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

        if (IsOpenStatus(entry.Status) && now > entry.QualifiedAtUtc + HoldingWindow(entry.Timeframe))
            entry = entry with { Status = "Expired", ClosedAtUtc = now, RealizedR = 0m };

        _entries[index] = entry;
        if (!IsOpenStatus(entry.Status)) _openKeyToEntryId.Remove(key);
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

    private void Persist()
    {
        List<QualificationLogEntry> snapshot;
        lock (_lock) snapshot = new List<QualificationLogEntry>(_entries);
        DiskCache.Save(_path, snapshot);
    }
}
