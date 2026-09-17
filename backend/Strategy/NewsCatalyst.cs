using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

public enum NewsCatalystState { Aligned, Mixed, Conflict, Unchecked, Unavailable }

public record NewsCatalystResult(NewsCatalystState State, string Reason);

// Section 22: news provides context, never manufactures a signal on its own.
// This only ever reads the ORIGINAL headline/source/sentiment fields the
// provider returned - it never generates or rewrites a summary.
public static class NewsCatalystEvaluator
{
    private const double MinRelevance = 0.3; // Alpha Vantage's own relevance_score scale is 0-1
    private const double SentimentThreshold = 0.15; // Alpha Vantage's documented neutral band is roughly [-0.15, 0.15]

    public static NewsCatalystResult Evaluate(NewsResult news, SetupDirection direction)
    {
        if (!news.Available)
            return new(NewsCatalystState.Unavailable, news.UnavailableReason ?? "News unavailable");

        var relevant = news.Items
            .Where(i => i.SentimentScore.HasValue && i.RelevanceScore.HasValue && i.RelevanceScore >= MinRelevance)
            .ToList();
        if (relevant.Count == 0)
            return new(NewsCatalystState.Unchecked, "No sufficiently relevant news items with sentiment data were returned");

        var avgSentiment = relevant.Average(i => i.SentimentScore!.Value);
        var isLong = direction == SetupDirection.Long;
        var bullish = avgSentiment > SentimentThreshold;
        var bearish = avgSentiment < -SentimentThreshold;

        if ((isLong && bullish) || (!isLong && bearish))
            return new(NewsCatalystState.Aligned, $"Average relevant sentiment {avgSentiment:0.00} (of {relevant.Count} item(s)) supports the {direction} direction");
        if ((isLong && bearish) || (!isLong && bullish))
            return new(NewsCatalystState.Conflict, $"Average relevant sentiment {avgSentiment:0.00} (of {relevant.Count} item(s)) opposes the {direction} direction");
        return new(NewsCatalystState.Mixed, $"Average relevant sentiment {avgSentiment:0.00} (of {relevant.Count} item(s)) is neutral relative to the {direction} direction");
    }
}
