using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Providers;

public interface INewsProvider
{
    Task<NewsResult> GetNewsAsync(string canonicalSymbol, CancellationToken ct = default);
}
