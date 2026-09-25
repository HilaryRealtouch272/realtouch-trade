using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class NewsEvidenceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private static NewsItem Item(int minutesAgo, double? relevance, double? sentiment, string url = "u") =>
        new($"Headline {minutesAgo}", "src", $"https://x/{url}{minutesAgo}", Now.AddMinutes(-minutesAgo), Array.Empty<string>(), relevance, sentiment);

    [Fact]
    public void HeadlinesOlderThanTwelveHoursNeverCountTowardTheState()
    {
        // Three strongly bearish headlines, all 13+ hours old, must not make a long a Conflict.
        var news = new NewsResult(true, null, new[] { Item(800, 0.9, -0.6), Item(820, 0.9, -0.6), Item(900, 0.9, -0.6) });

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Long, Now);

        Assert.Equal(NewsCatalystState.Unchecked, result.State);
        Assert.Contains("none is newer than 12 hours", result.Reason);
        Assert.Equal(0, result.Evidence!.ItemsCounted);
        Assert.Equal(3, result.Evidence.ItemsSeen);
    }

    [Fact]
    public void TheEvidenceRecordsWhichHeadlinesCountedTheirTimesRelevanceAndSentiment()
    {
        var news = new NewsResult(true, null, new[]
        {
            Item(30, 0.8, -0.4), Item(90, 0.6, -0.3), Item(200, 0.7, -0.5),
            Item(40, null, 0.9),          // Marketaux-style: sentiment but no relevance -> seen, not counted
            Item(900, 0.9, 0.9)           // too old -> seen, not counted
        });

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Short, Now);
        var e = result.Evidence!;

        Assert.Equal(NewsCatalystState.Aligned, result.State);
        Assert.Equal(5, e.ItemsSeen);
        Assert.Equal(3, e.ItemsCounted);
        Assert.Equal(-0.4, e.AverageSentiment!.Value, 3);
        Assert.Equal(0.7, e.AverageRelevance!.Value, 3);
        Assert.Equal(Now.AddMinutes(-30), e.NewestPublishedUtc);
        Assert.Equal(Now.AddMinutes(-200), e.OldestCountedUtc);
        Assert.Equal(3, e.Items.Count(i => i.Counted));
        Assert.False(e.Items.First(i => i.Relevance is null).Counted);
    }
}
