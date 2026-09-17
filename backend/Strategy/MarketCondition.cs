using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Uses the product's required label - never "Regime".
public enum MarketCondition
{
    TrendingBullish, TrendingBearish,
    BreakoutBullish, BreakoutBearish,
    ReversalDeveloping,
    Ranging,
    NeutralOrTransition
}

// Section 6 classification. Structure (BOS/CHoCH) is the primary input;
// EMA/ADX support it but never substitute for it (section 4).
public static class MarketConditionClassifier
{
    private const decimal BreakoutAtrMultiplier = 0.15m;
    private const decimal MinBodyRatioForBreakout = 0.60m;
    private const decimal AdxTrendingThreshold = 20m;

    public static MarketCondition Classify(IReadOnlyList<NormalizedCandle> allCandles, Timeframe timeframe, StructureResult structure)
    {
        var completed = allCandles.Where(c => c.IsComplete && c.Quality == DataQuality.Ok)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        if (completed.Count < 60) return MarketCondition.NeutralOrTransition;

        var ema20 = Indicators.Ema(completed, 20);
        var ema50 = Indicators.Ema(completed, 50);
        var adx = Indicators.Adx(completed, 14);
        var atr = Indicators.Atr(completed, 14);

        var lastIdx = completed.Count - 1;
        var last = completed[lastIdx];
        var lastAdx = adx[lastIdx];
        var lastAtr = atr[lastIdx];
        var emaSlope = ema20[lastIdx] - ema20[Math.Max(0, lastIdx - 5)];

        var lastEvent = structure.Events.LastOrDefault();
        var recentEvent = lastEvent is not null && lastEvent.CandleTimeUtc >= last.CloseTimeUtc.AddTicks(-TimeframeConfig.Duration(timeframe).Ticks * 3)
            ? lastEvent : null;

        // Breakout: a recent BOS with displacement quality on the breaking candle.
        if (recentEvent is not null && lastAtr is not null)
        {
            var isBullishBreak = recentEvent.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch;
            var breakCandle = completed.FirstOrDefault(c => c.CloseTimeUtc == recentEvent.CandleTimeUtc);
            if (breakCandle is not null)
            {
                var range = breakCandle.High - breakCandle.Low;
                var bodyRatio = range == 0 ? 0 : Math.Abs(breakCandle.Close - breakCandle.Open) / range;
                var distanceBeyondPivot = Math.Abs(recentEvent.ClosePrice - recentEvent.BrokenPivot.Price);

                if (bodyRatio >= MinBodyRatioForBreakout && distanceBeyondPivot >= BreakoutAtrMultiplier * lastAtr.Value)
                {
                    // A CHoCH-driven break after a sweep reads as a developing reversal, not a fresh breakout.
                    if (recentEvent.Type is StructureEventType.BullishChoch or StructureEventType.BearishChoch)
                        return MarketCondition.ReversalDeveloping;

                    return isBullishBreak ? MarketCondition.BreakoutBullish : MarketCondition.BreakoutBearish;
                }
            }
        }

        // Trending: confirmed structural trend + EMA alignment/slope + ADX.
        if (structure.FinalTrend == TrendState.Bullish && ema20[lastIdx] > ema50[lastIdx] && emaSlope > 0 && (lastAdx is null || lastAdx >= AdxTrendingThreshold))
            return MarketCondition.TrendingBullish;
        if (structure.FinalTrend == TrendState.Bearish && ema20[lastIdx] < ema50[lastIdx] && emaSlope < 0 && (lastAdx is null || lastAdx >= AdxTrendingThreshold))
            return MarketCondition.TrendingBearish;

        // Ranging: low ADX, no sustained BOS in either direction.
        var recentEvents = structure.Events.Where(e => e.CandleTimeUtc >= last.CloseTimeUtc.AddTicks(-TimeframeConfig.Duration(timeframe).Ticks * 20)).ToList();
        var alternating = recentEvents.Select(e => e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch).Distinct().Count() > 1;
        if ((lastAdx is null || lastAdx < AdxTrendingThreshold) && (recentEvents.Count == 0 || alternating))
            return MarketCondition.Ranging;

        return MarketCondition.NeutralOrTransition;
    }
}
