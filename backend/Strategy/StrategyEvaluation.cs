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
    SetupDirection? Direction = null
)
{
    // Qualified = detected, every mandatory gate passed, AND the score
    // cleared this model's own threshold (75/75/78/78 per section 6) - not
    // just "scored well" on a candidate whose structural preconditions
    // never actually held.
    public bool Qualified => Detected && MandatoryGatesPassed && Score >= Threshold;
    public int IndependentFamilyCount => ConfluenceFamilies.Count(f => f.Points > 0);
}
