using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Models;

// Section 27: the full per-setup response shape. This is the target shape -
// it is NOT yet returned by any live endpoint. Wiring it up needs the
// multi-timeframe orchestration from section 7 (to fill DataFreshness/context
// fields honestly across timeframes) and a decision on where lifecycle state
// is persisted (section 26) - both still pending. Built and tested as a
// standalone assembly step (SignalResultBuilder) so the shape is proven
// correct now rather than designed blind when wiring finally happens.
public record SignalResult(
    string SignalId,
    string StrategyVersion,
    string Symbol,
    string AssetClass,
    string DataProvider,
    string AnalysisTimeframe,
    string EntryTimeframe,
    SetupDirection Direction,
    MarketCondition Condition,
    SetupModelType SetupModel,
    SignalState Status,
    int SetupQualityScore,
    string Grade,
    DateTime DetectedAtUtc,
    DateTime ExpiryUtc,
    decimal LivePrice,
    decimal EntryZoneMin,
    decimal EntryZoneMax,
    decimal PreferredEntry,
    bool Triggered,
    decimal Stop,
    decimal Tp1,
    decimal Tp2,
    decimal Tp3,
    decimal RewardToRisk,
    decimal RiskPercent,
    decimal RiskAmount,
    decimal PositionSize,
    IReadOnlyList<KeyLevel> KeyLevels,
    IReadOnlyList<ConfluenceFamilyScore> ConfluenceFamilies,
    string ReasoningSummary,
    string NewsState,
    string EconomicCalendarState,
    string VolatilityState,
    string SessionState,
    string DataFreshness,
    string InvalidationConditions,
    // Non-null only for a signal that is tradable through the universal
    // score floor rather than the model's own gates and threshold: says
    // plainly what was unconfirmed, so an alert or ledger row can never be
    // mistaken for a fully confirmed trade.
    string? ScoreFloorNote = null,
    string? ScoringProfileId = null
);

public static class SignalResultBuilder
{
    public static SignalResult Build(
        string strategyVersion, string symbol, string assetClass, string dataProvider,
        Timeframe analysisTimeframe, SetupCandidate candidate, MarketCondition condition,
        TradePlan tradePlan, ConfluenceScoreResult score, PositionSizeResult positionSize,
        SignalState status, IReadOnlyList<KeyLevel> keyLevels, decimal livePrice, DateTime nowUtc,
        CalendarVetoResult? calendarVeto = null, NewsCatalystResult? newsCatalyst = null, string? scoreFloorNote = null, string? scoringProfileId = null)
    {
        var id = $"{symbol}:{TimeframeIntervals.Label(analysisTimeframe)}:{candidate.Model}:{nowUtc:yyyyMMddHHmmss}";
        var reasoning = string.Join(" ", candidate.Requirements
            .Where(r => r.Status != Strategy.RequirementStatus.NotEvaluated)
            .Select(r => $"{r.Description}: {r.Status}."));

        return new SignalResult(
            SignalId: id,
            StrategyVersion: strategyVersion,
            Symbol: symbol,
            AssetClass: assetClass,
            DataProvider: dataProvider,
            AnalysisTimeframe: TimeframeIntervals.Label(analysisTimeframe),
            EntryTimeframe: TimeframeIntervals.Label(tradePlan.Entry.EntryTimeframe),
            Direction: candidate.Direction,
            Condition: condition,
            SetupModel: candidate.Model,
            Status: status,
            SetupQualityScore: score.TotalScore,
            Grade: score.Grade,
            DetectedAtUtc: nowUtc,
            ExpiryUtc: tradePlan.Entry.ExpiryUtc,
            LivePrice: livePrice,
            EntryZoneMin: tradePlan.Entry.ZoneMin,
            EntryZoneMax: tradePlan.Entry.ZoneMax,
            PreferredEntry: tradePlan.Entry.PreferredEntry,
            Triggered: tradePlan.Entry.Triggered,
            Stop: tradePlan.Stop.Price,
            Tp1: tradePlan.Targets.Tp1,
            Tp2: tradePlan.Targets.Tp2,
            Tp3: tradePlan.Targets.Tp3,
            RewardToRisk: tradePlan.RewardToRisk,
            RiskPercent: positionSize.RiskPercentUsed,
            RiskAmount: positionSize.RiskAmount,
            PositionSize: positionSize.PositionSize,
            KeyLevels: keyLevels,
            ConfluenceFamilies: score.Families,
            ReasoningSummary: reasoning,
            NewsState: newsCatalyst is null ? "Unchecked - no news state supplied for this evaluation" : $"{newsCatalyst.State}: {newsCatalyst.Reason}",
            EconomicCalendarState: calendarVeto is null ? "Unavailable - no calendar state supplied for this evaluation" : $"{calendarVeto.State}: {calendarVeto.Reason}",
            VolatilityState: "Unavailable - historical volatility computation (section 23) not wired",
            SessionState: "Unavailable - IANA-timezone session engine (section 23) not wired",
            DataFreshness: "Single-timeframe only - cross-timeframe freshness check (section 7) not wired",
            // Rounded for display: raw ATR-derived decimals can carry 10+
            // digits of arithmetic noise (see Indicators.Atr) that has no
            // business reaching a user-facing string.
            InvalidationConditions: $"Price closes beyond {Math.Round(tradePlan.Entry.InvalidationPrice, 5)} before trigger, or beyond stop {Math.Round(tradePlan.Stop.Price, 5)} after trigger. {tradePlan.Stop.Reason}.",
            ScoreFloorNote: scoreFloorNote,
            ScoringProfileId: scoringProfileId
        );
    }
}
