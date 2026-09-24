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
    bool ScoreFloorQualified = false
)
{
    // Owner's explicit decision: any detected setup with a real trade plan
    // (entry, stop, targets) scoring this or higher is a tradable signal,
    // logged and tracked, regardless of unconfirmed mandatory gates.
    public const int ScoreFloor = 76;

    // Whether an evaluation is promoted by the floor rather than qualifying
    // normally. A setup that already qualifies through its own gates and
    // threshold is NOT "promoted" - it stays a fully confirmed signal. A
    // plan-less evaluation can never be promoted (nothing to trade or track).
    public static bool IsPromotedByScoreFloor(bool gatesPassed, int score, int threshold, bool tradePlanExists) =>
        tradePlanExists && score >= ScoreFloor && !(gatesPassed && score >= threshold);

    // Qualified = detected AND either (a) every mandatory gate passed and the
    // score cleared this model's own threshold (75/75/78/78 per section 6),
    // or (b) promoted by the universal score floor above.
    public bool Qualified => Detected && (ScoreFloorQualified || (MandatoryGatesPassed && Score >= Threshold));
    public int IndependentFamilyCount => ConfluenceFamilies.Count(f => f.Points > 0);
}
