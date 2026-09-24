using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// Section 9: a diagnostic record for every evaluated model, on every scan -
// qualified and rejected alike, so it's possible to tell a genuine lack of
// opportunities apart from an implementation fault (e.g. "Range Boundary
// Rejection produced zero candidates all week" vs "Range Boundary Rejection
// scored 73 forty times, one point under its own threshold").
public record DiagnosticsRecord(
    string Symbol,
    string Timeframe,
    DateTime ScanTimeUtc,
    SetupModelType StrategyId,
    Strategy.MarketCondition Condition,
    bool Detected,
    bool MandatoryGatesPassed,
    int Score,
    string Grade,
    int Threshold,
    IReadOnlyList<ReasonCode> FailedGates,
    IReadOnlyList<ReasonCode> Warnings,
    // Defaulted so diagnostics already persisted before the score floor
    // existed still deserialize. Everything from here on is appended, never
    // inserted: reason codes and these fields are persisted.
    bool ScoreFloorQualified = false,
    // Qualified | QualifiedNotSelected | VetoedByCalendar | Rejected | NoSetup
    string? FinalDisposition = null,
    // "Family +points/max" for every family that scored, and "Family (points/max)"
    // for every family that fell short: what the score had and what it lacked.
    IReadOnlyList<string>? AwardedEvidence = null,
    IReadOnlyList<string>? MissingEvidence = null,
    decimal? RewardToRisk = null,
    string? ScoringProfileId = null,
    int? IndependentFamilies = null,
    string? CalendarState = null,
    string? Session = null,
    int? MinutesToNextHighImpact = null,
    double? DataFreshnessMinutes = null
);

// Scan-level context shared by every model evaluated in one scan.
public record DiagnosticsContext(
    string CalendarState, string Session, int? MinutesToNextHighImpact, double? DataFreshnessMinutes,
    SetupModelType? PrimaryModel, bool HardVeto);

// One model's tally for one UTC day. A class (not a record) because the daily
// file is updated in place on every scan and round-tripped through JSON.
public class ModelDayRollup
{
    public int Evaluations { get; set; }
    public int Detected { get; set; }
    public int Qualified { get; set; }
    public int NearMiss { get; set; }
    public long ScoreSum { get; set; }
    public Dictionary<string, int> Gates { get; set; } = new();
}

public class StrategyDiagnosticsStore(IHostEnvironment env)
{
    private const int MaxEntries = 5000;
    private readonly string _path = Path.Combine(env.ContentRootPath, ".cache", "strategy-diagnostics.json");
    private readonly List<DiagnosticsRecord> _entries = DiskCache.Load<List<DiagnosticsRecord>>(
        Path.Combine(env.ContentRootPath, ".cache", "strategy-diagnostics.json")) ?? new();
    private readonly object _lock = new();

    // date (yyyy-MM-dd UTC) -> model name -> tally. Unlike the rolling window above
    // (about ten hours) this accumulates, so a week of "why no signals" is answerable.
    private const int MaxDays = 60;
    private readonly string _dailyPath = Path.Combine(env.ContentRootPath, ".cache", "diagnostics-daily.json");
    private readonly Dictionary<string, Dictionary<string, ModelDayRollup>> _daily = DiskCache.Load<Dictionary<string, Dictionary<string, ModelDayRollup>>>(
        Path.Combine(env.ContentRootPath, ".cache", "diagnostics-daily.json")) ?? new();

    internal static string Disposition(StrategyEvaluation e, DiagnosticsContext? ctx)
    {
        if (!e.Detected) return "NoSetup";
        if (!e.Qualified) return "Rejected";
        if (ctx?.HardVeto == true) return "VetoedByCalendar";
        return ctx?.PrimaryModel is null || ctx.PrimaryModel == e.StrategyId ? "Qualified" : "QualifiedNotSelected";
    }

    public void Record(string symbol, string timeframe, DateTime scanTimeUtc, Strategy.MarketCondition condition,
        IReadOnlyList<StrategyEvaluation> evaluations, DiagnosticsContext? context = null)
    {
        lock (_lock)
        {
            var day = scanTimeUtc.ToString("yyyy-MM-dd");
            if (!_daily.TryGetValue(day, out var models)) _daily[day] = models = new();

            foreach (var e in evaluations)
            {
                var detected = e.Detected;
                var awarded = detected ? e.ConfluenceFamilies.Where(f => f.Points > 0).Select(f => $"{f.Family} +{f.Points}/{f.MaxPoints}").ToList() : null;
                var missing = detected ? e.ConfluenceFamilies.Where(f => f.Points < f.MaxPoints).Select(f => $"{f.Family} ({f.Points}/{f.MaxPoints})").ToList() : null;

                _entries.Add(new DiagnosticsRecord(symbol, timeframe, scanTimeUtc, e.StrategyId, condition,
                    e.Detected, e.MandatoryGatesPassed, e.Score, e.Grade, e.Threshold, e.FailedGates, e.Warnings, e.ScoreFloorQualified,
                    Disposition(e, context), awarded, missing, e.RewardToRisk, e.ScoringProfileId,
                    detected ? e.IndependentFamilyCount : null,
                    context?.CalendarState, context?.Session, context?.MinutesToNextHighImpact, context?.DataFreshnessMinutes));

                var name = e.StrategyId.ToString();
                if (!models.TryGetValue(name, out var tally)) models[name] = tally = new ModelDayRollup();
                tally.Evaluations++;
                if (detected) { tally.Detected++; tally.ScoreSum += e.Score; }
                if (e.Qualified) tally.Qualified++;
                else if (detected && e.MandatoryGatesPassed && e.Score < e.Threshold && e.Score >= e.Threshold - 5) tally.NearMiss++;
                if (detected && !e.Qualified)
                    foreach (var gate in e.FailedGates.Distinct())
                        tally.Gates[gate.ToString()] = tally.Gates.GetValueOrDefault(gate.ToString()) + 1;
            }
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            foreach (var old in _daily.Keys.OrderByDescending(k => k).Skip(MaxDays).ToList()) _daily.Remove(old);
            DiskCache.Save(_path, _entries);
            DiskCache.Save(_dailyPath, _daily);
        }
    }

    public IReadOnlyDictionary<string, Dictionary<string, ModelDayRollup>> GetDaily()
    {
        lock (_lock) return _daily.OrderByDescending(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // What the static site serves in place of the live diagnostics API: the
    // summary, the accumulated daily rollup and the most recent detailed records.
    public object BuildSnapshot(int recent = 300)
    {
        lock (_lock)
            return new
            {
                generatedAtUtc = DateTime.UtcNow,
                summary = Summarize(),
                daily = GetDaily(),
                recent = _entries.OrderByDescending(e => e.ScanTimeUtc).Where(e => e.Detected).Take(recent).ToList()
            };
    }

    public IReadOnlyList<DiagnosticsRecord> GetAll()
    {
        lock (_lock) return _entries.OrderByDescending(e => e.ScanTimeUtc).ToList();
    }

    // Section 9's diagnostics view content, computed on demand rather than
    // maintained incrementally - this store is read far less often than
    // it's written, so recomputing per request keeps the write path simple.
    public StrategyDiagnosticsSummary Summarize()
    {
        lock (_lock)
        {
            var byModel = _entries.GroupBy(e => e.StrategyId).ToList();
            var models = byModel.Select(g =>
            {
                var detected = g.Count(e => e.Detected);
                var qualified = g.Count(e => e.Detected && e.MandatoryGatesPassed && e.Score >= e.Threshold);
                var rejected = detected - qualified;
                var avgScore = g.Where(e => e.Detected).Select(e => (double)e.Score).DefaultIfEmpty(0).Average();
                // Near-miss: the structural pattern was genuinely there
                // (gates passed) and the score fell short of qualifying by
                // 5 points or less - distinguishes "almost qualified" from
                // "structurally absent", per section 9's requirement.
                var nearMiss = g.Count(e => e.Detected && e.MandatoryGatesPassed && e.Score < e.Threshold && e.Score >= e.Threshold - 5);
                var topReasons = g.SelectMany(e => e.FailedGates)
                    .GroupBy(r => r)
                    .OrderByDescending(r => r.Count())
                    .Take(3)
                    .Select(r => $"{r.Key} ({r.Count()})")
                    .ToList();
                return new ModelDiagnosticsSummary(g.Key, g.Count(), detected, qualified, rejected, Math.Round(avgScore, 1), nearMiss, topReasons);
            }).ToList();

            return new StrategyDiagnosticsSummary(_entries.Count, models);
        }
    }
}

public record ModelDiagnosticsSummary(
    SetupModelType StrategyId, int TotalScans, int Detected, int Qualified, int Rejected,
    double AverageScore, int NearMissCount, IReadOnlyList<string> TopRejectionReasons);

public record StrategyDiagnosticsSummary(int TotalRecords, IReadOnlyList<ModelDiagnosticsSummary> Models);
