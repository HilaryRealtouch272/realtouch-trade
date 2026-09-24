namespace RealtouchSmartTrade.Api.Strategy;

// Market Condition routing: which model may qualify in which condition. A
// signal must have a resolved Market Condition, so NeutralOrTransition (the
// classifier's "no clear state") supports no model and is a non-trade. The
// table is deliberately explicit and small: every entry is a documented choice,
// and the count of MARKET_CONDITION_NOT_SUPPORTED per model in diagnostics shows
// whether a row is too strict.
public static class ModelRouting
{
    private static readonly IReadOnlyDictionary<SetupModelType, MarketCondition[]> Supported =
        new Dictionary<SetupModelType, MarketCondition[]>
        {
            // Pullbacks in a confirmed trend.
            [SetupModelType.TrendContinuationPullback] = new[] { MarketCondition.TrendingBullish, MarketCondition.TrendingBearish },
            // A break and its retest: the break label lasts only a few candles, after
            // which the market usually reads as trending.
            [SetupModelType.BreakoutAndRetest] = new[]
            {
                MarketCondition.BreakoutBullish, MarketCondition.BreakoutBearish,
                MarketCondition.TrendingBullish, MarketCondition.TrendingBearish
            },
            // Counter-trend by nature: a sweep and reversal at the end of a trend or the
            // edge of a range, or once a reversal is already developing.
            [SetupModelType.LiquiditySweepReversal] = new[]
            {
                MarketCondition.ReversalDeveloping, MarketCondition.Ranging,
                MarketCondition.TrendingBullish, MarketCondition.TrendingBearish
            },
            [SetupModelType.RangeBoundaryRejection] = new[] { MarketCondition.Ranging },
        };

    public static bool Supports(SetupModelType model, MarketCondition condition) =>
        Supported.TryGetValue(model, out var allowed) && allowed.Contains(condition);

    // The condition each model is "at home" in; used only as a tiebreak between
    // equal scores, never to let an unsupported model qualify.
    public static bool IsNative(SetupModelType model, MarketCondition condition) => model switch
    {
        SetupModelType.TrendContinuationPullback => condition is MarketCondition.TrendingBullish or MarketCondition.TrendingBearish,
        SetupModelType.BreakoutAndRetest => condition is MarketCondition.BreakoutBullish or MarketCondition.BreakoutBearish,
        SetupModelType.LiquiditySweepReversal => condition is MarketCondition.ReversalDeveloping,
        SetupModelType.RangeBoundaryRejection => condition is MarketCondition.Ranging,
        _ => false
    };
}
