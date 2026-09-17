using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum HtfAlignment { Aligned, Neutral, Conflicting }

// Section 7's mapping table. EntryTimeframe picks the finer of the two
// options the spec allows for Daily ("1-hour or 15-minute") - a documented
// simplification; the spec leaves that choice open, this always takes 15m.
public static class TimeframeHierarchy
{
    public static Timeframe[] ContextTimeframes(Timeframe displayed) => displayed switch
    {
        Timeframe.Weekly => new[] { Timeframe.Weekly, Timeframe.Daily },
        Timeframe.Daily => new[] { Timeframe.Weekly, Timeframe.Daily },
        Timeframe.H4 => new[] { Timeframe.Daily, Timeframe.H4 },
        Timeframe.H1 => new[] { Timeframe.H4, Timeframe.H1 },
        Timeframe.M15 => new[] { Timeframe.Daily, Timeframe.H4 },
        _ => throw new ArgumentOutOfRangeException(nameof(displayed))
    };

    public static Timeframe EntryTimeframe(Timeframe displayed) => displayed switch
    {
        Timeframe.Weekly => Timeframe.H4,
        Timeframe.Daily => Timeframe.M15,
        Timeframe.H4 => Timeframe.M15,
        Timeframe.H1 => Timeframe.M15,
        Timeframe.M15 => Timeframe.M15,
        _ => throw new ArgumentOutOfRangeException(nameof(displayed))
    };

    // Section 7: "Where Weekly and Daily strongly conflict, reduce the setup
    // grade or keep the market on the watchlist." Aligned = every context TF's
    // trend matches the setup direction. Conflicting = any context TF trends
    // the OPPOSITE direction (a real, actionable disagreement). Neutral =
    // context is Ranging/Breakout/Transition - no directional opinion either way.
    public static HtfAlignment Evaluate(SetupDirection direction, IReadOnlyDictionary<Timeframe, MarketCondition> contextConditions)
    {
        var matches = contextConditions.Values.Select(c => Classify(c, direction)).ToList();
        if (matches.Any(m => m == -1)) return HtfAlignment.Conflicting;
        if (matches.All(m => m == 1)) return HtfAlignment.Aligned;
        return HtfAlignment.Neutral;
    }

    // 1 = supports direction, -1 = opposes direction, 0 = no directional opinion.
    private static int Classify(MarketCondition condition, SetupDirection direction)
    {
        var isLong = direction == SetupDirection.Long;
        var bullish = condition is MarketCondition.TrendingBullish or MarketCondition.BreakoutBullish;
        var bearish = condition is MarketCondition.TrendingBearish or MarketCondition.BreakoutBearish;
        if (bullish) return isLong ? 1 : -1;
        if (bearish) return isLong ? -1 : 1;
        return 0;
    }
}
