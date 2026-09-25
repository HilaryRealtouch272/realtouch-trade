using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class NewsCatalystEvaluatorTests
{
    private static NewsItem Item(double? relevance, double? sentiment) =>
        new("Test headline", "Test Source", "https://example.com", DateTime.UtcNow, Array.Empty<string>(), relevance, sentiment);

    // Enough scored headlines (the minimum) all carrying the same read.
    private static NewsItem[] Enough(double relevance, double sentiment) =>
        Enumerable.Range(0, NewsCatalystEvaluator.MinScoredItems).Select(_ => Item(relevance, sentiment)).ToArray();

    [Fact]
    public void ReturnsUnavailableWhenTheNewsProviderItselfIsUnavailable()
    {
        var news = new NewsResult(false, "Alpha Vantage API key not configured", Array.Empty<NewsItem>());

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Long);

        Assert.Equal(NewsCatalystState.Unavailable, result.State);
    }

    [Fact]
    public void ReturnsUncheckedWhenNoItemsHaveUsableSentimentData()
    {
        var news = new NewsResult(true, null, new[] { Item(null, null), Item(0.1, 0.5) }); // relevance below threshold

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Long);

        Assert.Equal(NewsCatalystState.Unchecked, result.State);
    }

    [Fact]
    public void PositiveSentimentAlignsWithALongSetup()
    {
        var news = new NewsResult(true, null, Enough(0.8, 0.4));

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Long);

        Assert.Equal(NewsCatalystState.Aligned, result.State);
    }

    [Fact]
    public void PositiveSentimentConflictsWithAShortSetup()
    {
        var news = new NewsResult(true, null, Enough(0.8, 0.4));

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Short);

        Assert.Equal(NewsCatalystState.Conflict, result.State);
    }

    [Fact]
    public void NegativeSentimentAlignsWithAShortSetup()
    {
        var news = new NewsResult(true, null, Enough(0.8, -0.4));

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Short);

        Assert.Equal(NewsCatalystState.Aligned, result.State);
    }

    [Fact]
    public void NearZeroSentimentIsMixedRegardlessOfDirection()
    {
        var news = new NewsResult(true, null, Enough(0.8, 0.05));

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Long);

        Assert.Equal(NewsCatalystState.Mixed, result.State);
    }

    [Fact]
    public void AveragesSentimentAcrossMultipleRelevantItems()
    {
        var news = new NewsResult(true, null, new[] { Item(0.9, 0.5), Item(0.9, -0.5), Item(0.9, 0.5), Item(0.9, -0.5) }); // averages to ~0 -> Mixed

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Long);

        Assert.Equal(NewsCatalystState.Mixed, result.State);
    }

    [Fact]
    public void LowRelevanceItemsAreExcludedFromTheAverage()
    {
        // One highly relevant bearish item, one irrelevant (low-relevance) bullish item -
        // the irrelevant one must not drag the average toward neutral/bullish.
        var news = new NewsResult(true, null, new[] { Item(0.9, -0.5), Item(0.9, -0.5), Item(0.9, -0.5), Item(0.05, 0.9) });

        var result = NewsCatalystEvaluator.Evaluate(news, SetupDirection.Short);

        Assert.Equal(NewsCatalystState.Aligned, result.State);
    }

    [Fact]
    public void OneOrTwoScoredHeadlinesAreThinNotAlignedOrConflict()
    {
        // The BTC case: a single headline (about USDT) was enough to call a short a Conflict.
        var one = new NewsResult(true, null, new[] { Item(0.9, 0.19) });
        var two = new NewsResult(true, null, new[] { Item(0.9, 0.5), Item(0.9, 0.6) });

        Assert.Equal(NewsCatalystState.Thin, NewsCatalystEvaluator.Evaluate(one, SetupDirection.Short).State);
        Assert.Equal(NewsCatalystState.Thin, NewsCatalystEvaluator.Evaluate(two, SetupDirection.Short).State);
    }

    [Fact]
    public void UnscoredOrLowRelevanceItemsDoNotCountTowardTheMinimum()
    {
        var news = new NewsResult(true, null, new[] { Item(0.9, 0.5), Item(0.9, 0.5), Item(null, 0.5), Item(0.05, 0.5) });

        Assert.Equal(NewsCatalystState.Thin, NewsCatalystEvaluator.Evaluate(news, SetupDirection.Long).State);
    }
}
