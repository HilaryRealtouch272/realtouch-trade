using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

// The free news stack, in priority order:
//   1. Marketaux  - scored headlines (the only source that can move the news state)
//   2. Alpha Vantage - fallback for scored headlines when Marketaux is unavailable
//   3. GDELT      - always added, unscored geopolitical context
// The result is Available if ANY source answered; the unavailable reason lists
// every source that did not, so a silent gap is never hidden.
public class CompositeNewsProvider(
    MarketauxNewsProvider marketaux, AlphaVantageNewsProvider alphaVantage, GdeltNewsProvider gdelt) : INewsProvider
{
    public async Task<NewsResult> GetNewsAsync(string canonicalSymbol, CancellationToken ct = default)
    {
        var scored = await marketaux.GetNewsAsync(canonicalSymbol, ct);
        var failures = new List<string>();
        if (!scored.Available)
        {
            failures.Add(scored.UnavailableReason ?? "Marketaux unavailable");
            scored = await alphaVantage.GetNewsAsync(canonicalSymbol, ct);
            if (!scored.Available) failures.Add(scored.UnavailableReason ?? "Alpha Vantage unavailable");
        }

        var context = await gdelt.GetNewsAsync(canonicalSymbol, ct);
        if (!context.Available) failures.Add(context.UnavailableReason ?? "GDELT unavailable");

        return Merge(scored, context, failures);
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
