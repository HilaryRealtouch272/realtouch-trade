namespace RealtouchSmartTrade.Api.Strategy;

// Section 4's StrategyEvaluation, adapted to C#. Every evaluator (one per
// SetupModelType) returns exactly one of these per (symbol, timeframe) scan -
// never null, never silently skipped - so a strategy that produced nothing
// is distinguishable from one that was never actually run.
public record StrategyEvaluation(
    SetupModelType StrategyId,
    string Version,
    bool Detected,
    bool MandatoryGatesPassed,
    int Score,
    string Grade,
    int Threshold,
    IReadOnlyList<ConfluenceFamilyScore> ConfluenceFamilies,
    IReadOnlyList<ReasonCode> FailedGates,
    IReadOnlyList<ReasonCode> Warnings,
    SetupCandidate? Candidate,
    // Candidate itself is deliberately withheld (null) whenever mandatory
    // gates fail, since its entry/stop/target plan is not something that
    // should ever be treated as real. But the DIRECTION a model evaluated
    // is known the moment a candidate is merely detected - long before
    // gates or score are decided - and is real, useful information on its
    // own (a diagnostics view showing "this model scored 78" is misleading
    // without saying which way it was even looking). Populated whenever
    // Detected is true, independent of MandatoryGatesPassed/Candidate.
    SetupDirection? Direction = null,
    // True only when this evaluation is tradable BECAUSE of the universal
    // score floor, not because it cleared its own model's gates and
    // threshold: a real trade plan exists and the score is at least
    // ScoreFloor, but a mandatory gate / evaluation check is unconfirmed or
    // the score is under this model's own threshold. MandatoryGatesPassed
    // is deliberately left honest (false) in that case - the diagnostics
    // record must never claim gates passed when they did not.
    bool ScoreFloorQualified = false,
    // Which scoring profile produced this score (section 10): how volume was
    // treated for this asset class and data availability.
    string? ScoringProfileId = null
)
{
    // Qualified = detected, every mandatory gate passed (including the common
    // gates) and the score cleared this model's own threshold (75/75/78/78).
    // ScoreFloorQualified is a legacy field, always false now; it is kept only so
    // diagnostics persisted while the 76+ floor existed still deserialize.
    public bool Qualified => Detected && MandatoryGatesPassed && Score >= Threshold;
    public int IndependentFamilyCount => ConfluenceFamilies.Count(f => f.Points > 0);
}
