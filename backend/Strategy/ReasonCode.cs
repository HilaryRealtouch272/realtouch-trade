namespace RealtouchSmartTrade.Api.Strategy;

// Section 9's structured reason codes. Every StrategyEvaluation reports its
// failed gates and warnings using these, not free-text, so the diagnostics
// view can group/count rejections reliably (e.g. "most common rejection
// reason this week") instead of parsing prose.
public enum ReasonCode
{
    TREND_ALIGNMENT_MISSING,
    TREND_CONDITION_NOT_CONFIRMED,
    LOCATION_NOT_QUALIFIED,
    ZONE_NOT_FOUND,
    SWEEP_NOT_CONFIRMED,
    STRUCTURE_CONFIRMATION_MISSING,
    RANGE_NOT_ESTABLISHED,
    RANGE_BOUNDARY_NOT_REACHED,
    ENTRY_TOO_CLOSE_TO_EQUILIBRIUM,
    BREAKOUT_NOT_CONFIRMED,
    BREAKOUT_DISTANCE_INSUFFICIENT,
    RETEST_NOT_REACHED,
    RETEST_CONFIRMATION_MISSING,
    OPPOSING_LEVEL_BLOCKS_TARGET,
    SWEEP_CLOSE_NOT_CONFIRMED,
    CHOCH_MISSING,
    REVERSAL_LOCATION_MISSING,
    CONTROLLED_RETEST_MISSING,
    STOP_NOT_RATIONAL,
    REWARD_RISK_TOO_LOW,
    NEWS_VETO_ACTIVE,
    DATA_STALE,
    DATA_INSUFFICIENT,
    VOLUME_UNAVAILABLE,
    DUPLICATE_SIGNAL,
    NO_QUALIFYING_ZONE,
    HTF_CONFLICT,
    // Common gates (section 5). Appended, never inserted: diagnostics persist
    // reason codes as integers.
    INSUFFICIENT_CONFLUENCE,
    COST_TOO_HIGH,
    // Market Condition routing: the current condition is not one this model may trade in.
    MARKET_CONDITION_NOT_SUPPORTED
}
