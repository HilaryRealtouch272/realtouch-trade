using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Thin is appended, not inserted: too few scored headlines to call alignment either way.
public enum NewsCatalystState { Aligned, Mixed, Conflict, Unchecked, Unavailable, Thin }

// One headline that fed (or was seen by) the news state, kept so a trade can later be
// judged against exactly what the news looked like: when it was published, how
// relevant and how bullish or bearish it was scored.
public record NewsEvidenceItem(string Headline, string Source, DateTime PublishedUtc, double? Relevance, double? Sentiment, bool Counted);

public record NewsEvidence(
    int ItemsSeen, int ItemsCounted, double? AverageSentiment, double? AverageRelevance,
    DateTime? NewestPublishedUtc, DateTime? OldestCountedUtc, IReadOnlyList<NewsEvidenceItem> Items);

public record NewsCatalystResult(NewsCatalystState State, string Reason, NewsEvidence? Evidence = null);

// Section 22: news provides context, never manufactures a signal on its own.
// This only ever reads the ORIGINAL headline/source/sentiment fields the
// provider returned - it never generates or rewrites a summary.
public static class NewsCatalystEvaluator
{
    private const double MinRelevance = 0.3; // Alpha Vantage's own relevance_score scale is 0-1
    // One or two scored headlines is an anecdote, not a read of the news: fewer than this is Thin.
    internal const int MinScoredItems = 3;
    private const double SentimentThreshold = 0.15; // Alpha Vantage's documented neutral band is roughly [-0.15, 0.15]

    // Headlines older than this no longer describe the market the trade is entered into.
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private const int MaxEvidenceItems = 6;

    public static NewsCatalystResult Evaluate(NewsResult news, SetupDirection direction, DateTime? nowUtc = null)
    {
        if (!news.Available)
            return new(NewsCatalystState.Unavailable, news.UnavailableReason ?? "News unavailable");

        var now = nowUtc ?? DateTime.UtcNow;
        bool Counts(NewsItem i) => i.SentimentScore.HasValue && i.RelevanceScore.HasValue && i.RelevanceScore >= MinRelevance
                                   && now - i.PublishedUtc <= MaxAge;
        var relevant = news.Items.Where(Counts).ToList();
        var evidence = BuildEvidence(news.Items, relevant, Counts);

        if (relevant.Count == 0)
            return new(NewsCatalystState.Unchecked,
                news.Items.Any(i => i.SentimentScore.HasValue && i.RelevanceScore.HasValue && now - i.PublishedUtc > MaxAge)
                    ? $"Scored headlines exist but none is newer than {MaxAge.TotalHours:0} hours"
                    : "No sufficiently relevant news items with sentiment data were returned", evidence);

        if (relevant.Count < MinScoredItems)
            return new(NewsCatalystState.Thin, $"Only {relevant.Count} scored headline(s) - too few to call alignment (needs {MinScoredItems})", evidence);

        var avgSentiment = relevant.Average(i => i.SentimentScore!.Value);
        var isLong = direction == SetupDirection.Long;
        var bullish = avgSentiment > SentimentThreshold;
        var bearish = avgSentiment < -SentimentThreshold;

        if ((isLong && bullish) || (!isLong && bearish))
            return new(NewsCatalystState.Aligned, $"Average relevant sentiment {avgSentiment:0.00} (of {relevant.Count} item(s)) supports the {direction} direction", evidence);
        if ((isLong && bearish) || (!isLong && bullish))
            return new(NewsCatalystState.Conflict, $"Average relevant sentiment {avgSentiment:0.00} (of {relevant.Count} item(s)) opposes the {direction} direction", evidence);
        return new(NewsCatalystState.Mixed, $"Average relevant sentiment {avgSentiment:0.00} (of {relevant.Count} item(s)) is neutral relative to the {direction} direction", evidence);
    }

    private static NewsEvidence BuildEvidence(IReadOnlyList<NewsItem> all, List<NewsItem> counted, Func<NewsItem, bool> counts)
    {
        var items = all.OrderByDescending(i => i.PublishedUtc).Take(MaxEvidenceItems)
            .Select(i => new NewsEvidenceItem(i.Headline.Length > 110 ? i.Headline[..110] : i.Headline, i.Source, i.PublishedUtc,
                i.RelevanceScore, i.SentimentScore, counts(i))).ToList();
        return new NewsEvidence(all.Count, counted.Count,
            counted.Count > 0 ? counted.Average(i => i.SentimentScore!.Value) : null,
            counted.Count > 0 ? counted.Average(i => i.RelevanceScore!.Value) : null,
            all.Count > 0 ? all.Max(i => i.PublishedUtc) : null,
            counted.Count > 0 ? counted.Min(i => i.PublishedUtc) : null, items);
    }
}
