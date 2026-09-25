using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// The free news stack, in priority order:
//   1. Marketaux  - scored headlines (the only source that can move the news state)
//   2. Alpha Vantage - asked whenever Marketaux returned no SCORED item (unavailable or unscored)
//   3. GDELT      - always added, unscored geopolitical context
// The result is Available if ANY source answered; the unavailable reason lists
// every source that did not, so a silent gap is never hidden.
public class CompositeNewsProvider(
    MarketauxNewsProvider marketaux, AlphaVantageNewsProvider alphaVantage, GdeltNewsProvider gdelt) : INewsProvider
{
    public async Task<NewsResult> GetNewsAsync(string canonicalSymbol, CancellationToken ct = default)
    {
        var failures = new List<string>();
        var scored = await marketaux.GetNewsAsync(canonicalSymbol, ct);
        if (!scored.Available) failures.Add(scored.UnavailableReason ?? "Marketaux unavailable");

        // Marketaux only scores articles tied to a stock ticker, so for gold, FX and most crypto it
        // answers with headlines and NO sentiment. "Available" is not enough: without a scored item the
        // news state could only ever be Unchecked, so Alpha Vantage (which does score them) is asked too.
        if (!HasScoredItem(scored))
        {
            var fallback = await alphaVantage.GetNewsAsync(canonicalSymbol, ct);
            if (!fallback.Available) failures.Add(fallback.UnavailableReason ?? "Alpha Vantage unavailable");
            scored = Combine(scored, fallback);
        }

        var context = await gdelt.GetNewsAsync(canonicalSymbol, ct);
        if (!context.Available) failures.Add(context.UnavailableReason ?? "GDELT unavailable");

        return Merge(scored, context, failures);
    }

    internal static bool HasScoredItem(NewsResult r) => r.Available && r.Items.Any(i => i.SentimentScore.HasValue && i.RelevanceScore.HasValue);

    internal static NewsResult Combine(NewsResult a, NewsResult b)
    {
        if (!a.Available && !b.Available) return new NewsResult(false, a.UnavailableReason ?? b.UnavailableReason, Array.Empty<NewsItem>());
        return new NewsResult(true, null, a.Items.Concat(b.Items).ToList());
    }

    internal static NewsResult Merge(NewsResult scored, NewsResult context, IReadOnlyList<string> failures)
    {
        if (!scored.Available && !context.Available)
            return new NewsResult(false, string.Join(" | ", failures), Array.Empty<NewsItem>());

        var items = scored.Items.Concat(context.Items)
            .GroupBy(i => i.Url).Select(g => g.First())
            .OrderByDescending(i => i.PublishedUtc).ToList();
        return new NewsResult(true, null, items);
    }
}
