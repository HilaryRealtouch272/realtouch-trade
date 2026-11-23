using System.Text.Json;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Providers;

// Private S3 -> workflow download. No HTTP endpoint or trading credentials here.
public static class MarketHistoryBridge
{
    public static IReadOnlyList<NormalizedCandle> Load(string path, string symbol, Timeframe tf, DateTime now)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 1) throw new InvalidOperationException("Unsupported market snapshot");
        var generated = root.GetProperty("generatedAtUtc").GetDateTime();
        if (generated > now.AddMinutes(2) || now - generated > TimeSpan.FromMinutes(20))
            throw new InvalidOperationException("Market snapshot is stale or future-dated");
        var market = root.GetProperty("markets").GetProperty(symbol);
        if (market.TryGetProperty("error", out _)) throw new InvalidOperationException("Upstream market history unavailable");
        var provider = market.GetProperty("provider").GetString()!;
        var feed = market.GetProperty("feedSymbol").GetString()!;
        if ((symbol == "WTI/USD" && (provider != "Exness" || feed != "USOILm")) ||
            (symbol == SpxTrendRules.Symbol && (provider != "Yahoo" || feed != "^GSPC")))
            throw new InvalidOperationException("Unexpected market source");
        var intraday = symbol == "WTI/USD" && tf is Timeframe.M15 or Timeframe.H1 or Timeframe.H4;
        var candles = new List<NormalizedCandle>();
        foreach (var row in market.GetProperty(intraday ? "intraday" : "candles").EnumerateArray())
        {
            DateTime open, close;
            if (symbol == SpxTrendRules.Symbol)
            {
                open = DateTime.SpecifyKind(SpxTrendRules.SessionDate(DateTimeOffset.FromUnixTimeSeconds(row.GetProperty("timestamp").GetInt64()).UtcDateTime), DateTimeKind.Utc);
                close = SpxTrendRules.SessionClose(open);
            }
            else { open = row.GetProperty("openTimeUtc").GetDateTime(); close = row.GetProperty("closeTimeUtc").GetDateTime(); }
            var o = row.GetProperty("open").GetDecimal(); var h = row.GetProperty("high").GetDecimal();
            var l = row.GetProperty("low").GetDecimal(); var c = row.GetProperty("close").GetDecimal();
            if (o <= 0 || l <= 0 || l > Math.Min(o,c) || h < Math.Max(o,c) || h <= l || close <= open || open > now.AddMinutes(5))
                throw new InvalidOperationException("Invalid market candle");
            if (open.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            candles.Add(new(symbol, feed, provider, intraday ? Timeframe.M15 : Timeframe.Daily,
                open, close, o,h,l,c,null,close <= now,generated,DataQuality.Ok));
        }
        if (candles.Select(c => c.OpenTimeUtc).Distinct().Count() != candles.Count)
            throw new InvalidOperationException("Duplicate market candles");
        candles = candles.OrderBy(c => c.OpenTimeUtc).ToList();
        if (candles.Count < (intraday ? 60 : 220) || candles[^1].CloseTimeUtc < now.AddDays(-4))
            throw new InvalidOperationException("Market candle coverage insufficient or stale");
        if (tf == Timeframe.Daily || tf == Timeframe.M15) return candles;
        DateTime Bucket(DateTime t) => tf switch {
            Timeframe.H1 => t.Date.AddHours(t.Hour),
            Timeframe.H4 => t.Date.AddHours(t.Hour / 4 * 4),
            Timeframe.Weekly => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)),
            _ => throw new InvalidOperationException("Unsupported bridge timeframe") };
        return candles.GroupBy(c => Bucket(c.OpenTimeUtc)).Select(g => {
            var rows=g.ToList();var start=g.Key;var end=start+TimeframeConfig.Duration(tf);
            return new NormalizedCandle(symbol,feed,provider,tf,start,end,rows[0].Open,rows.Max(c=>c.High),
                rows.Min(c=>c.Low),rows[^1].Close,null,end<=now,generated,DataQuality.Ok);
        }).ToList();
    }
}
