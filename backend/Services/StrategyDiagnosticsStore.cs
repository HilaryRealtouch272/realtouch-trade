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
    IReadOnlyList<ReasonCode> Warnings
);

public class StrategyDiagnosticsStore(IHostEnvironment env)
{
    private const int MaxEntries = 5000;
    private readonly string _path = Path.Combine(env.ContentRootPath, ".cache", "strategy-diagnostics.json");
    private readonly List<DiagnosticsRecord> _entries = DiskCache.Load<List<DiagnosticsRecord>>(
        Path.Combine(env.ContentRootPath, ".cache", "strategy-diagnostics.json")) ?? new();
    private readonly object _lock = new();

    public void Record(string symbol, string timeframe, DateTime scanTimeUtc, Strategy.MarketCondition condition, IReadOnlyList<StrategyEvaluation> evaluations)
    {
        lock (_lock)
        {
            foreach (var e in evaluations)
            {
                _entries.Add(new DiagnosticsRecord(symbol, timeframe, scanTimeUtc, e.StrategyId, condition,
                    e.Detected, e.MandatoryGatesPassed, e.Score, e.Grade, e.Threshold, e.FailedGates, e.Warnings));
            }
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            DiskCache.Save(_path, _entries);
        }
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
