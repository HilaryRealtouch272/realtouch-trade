using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class NewsProvidersTests
{
    [Fact]
    public void MarketauxArticlesAreParsedWithSentimentOnlyWhenTheApiSuppliesIt()
    {
        var json = """
        {"data":[
          {"title":"Fed holds rates","url":"https://x/1","source":"reuters.com","published_at":"2026-09-24T08:30:00.000000Z",
           "entities":[{"sentiment_score":0.4},{"sentiment_score":0.2}]},
          {"title":"No entities","url":"https://x/2","source":"a.com","published_at":"2026-09-24T07:00:00.000000Z"},
          {"title":"","url":"https://x/3","published_at":"2026-09-24T07:00:00Z"}
        ]}
        """;

        var result = MarketauxNewsProvider.Parse("EUR/USD", json);

        Assert.True(result.Available);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(0.3, result.Items[0].SentimentScore!.Value, 3);
        Assert.Null(result.Items[1].SentimentScore);
        Assert.Equal(DateTimeKind.Utc, result.Items[0].PublishedUtc.Kind);
    }

    [Fact]
    public void AnUnexpectedMarketauxShapeIsUnavailableNotEmptyNews()
    {
        Assert.False(MarketauxNewsProvider.Parse("EUR/USD", "{\"error\":{\"code\":\"usage_limit_reached\"}}").Available);
    }

    [Fact]
    public void GdeltArticlesAreParsedWithoutAnySentiment()
    {
        var json = "{\"articles\":[{\"url\":\"https://g/1\",\"title\":\"BoE signals\",\"seendate\":\"20260924T093000Z\",\"domain\":\"ft.com\"}]}";

        var result = GdeltNewsProvider.Parse("GBP/USD", json);

        Assert.True(result.Available);
        var item = Assert.Single(result.Items);
        Assert.Null(item.SentimentScore);
        Assert.Equal(new DateTime(2026, 9, 24, 9, 30, 0, DateTimeKind.Utc), item.PublishedUtc);
    }

    [Fact]
    public void AGdeltRateLimitReplyIsUnavailableNotACrash()
    {
        var result = GdeltNewsProvider.Parse("GBP/USD", "Please limit requests to one every 5 seconds.");

        Assert.False(result.Available);
    }

    [Fact]
    public void TheCompositeIsAvailableIfAnySourceAnsweredAndListsEveryFailureOtherwise()
    {
        var down = new NewsResult(false, "quota", Array.Empty<NewsItem>());
        var ctx = new NewsResult(true, null, new[] { new NewsItem("h", "s", "https://u", DateTime.UtcNow, new[] { "EUR/USD" }, null, null) });

        Assert.True(CompositeNewsProvider.Merge(down, ctx, new[] { "Marketaux: quota" }).Available);

        var none = CompositeNewsProvider.Merge(down, down, new[] { "Marketaux: quota", "GDELT: down" });
        Assert.False(none.Available);
        Assert.Contains("GDELT: down", none.UnavailableReason);
    }

    [Fact]
    public void HeadlinesWithNoSentimentDoNotCountAsScoredSoTheScoredSourceIsStillAsked()
    {
        var unscored = new NewsResult(true, null, new[] { new NewsItem("h", "s", "https://u/1", DateTime.UtcNow, new[] { "XAU/USD" }, null, null) });
        var scoredItem = new NewsResult(true, null, new[] { new NewsItem("h2", "s", "https://u/2", DateTime.UtcNow, new[] { "XAU/USD" }, 0.8, -0.3) });

        Assert.False(CompositeNewsProvider.HasScoredItem(unscored));
        Assert.True(CompositeNewsProvider.HasScoredItem(scoredItem));
        // A sentiment with no relevance (Marketaux) is context only and does not count as scored.
        Assert.False(CompositeNewsProvider.HasScoredItem(new NewsResult(true, null, new[] { new NewsItem("h", "s", "https://u/3", DateTime.UtcNow, new[] { "BTC/USDT" }, null, 0.4) })));
        Assert.Equal(2, CompositeNewsProvider.Combine(unscored, scoredItem).Items.Count);
        Assert.True(CompositeNewsProvider.Combine(new NewsResult(false, "down", Array.Empty<NewsItem>()), scoredItem).Available);
    }
}
