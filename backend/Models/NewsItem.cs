namespace RealtouchSmartTrade.Api.Models;

// Section 22: news must provide context, never manufacture a signal itself.
public record NewsItem(
    string Headline,
    string Source,
    string Url,
    DateTime PublishedUtc,
    IReadOnlyList<string> AffectedAssets,
    double? RelevanceScore,
    double? SentimentScore
);

public record NewsResult(bool Available, string? UnavailableReason, IReadOnlyList<NewsItem> Items);
