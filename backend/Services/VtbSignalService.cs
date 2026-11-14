using RealtouchSmartTrade.Api.Backtesting;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// Validated Trend Breakout (VTB) - a daily SMA200-trend + Donchian-channel-breakout module,
// paper-traded for BTC/USDT, ETH/USDT, XAU/USD and WTI/USD.
//
// COMPLETELY ISOLATED from the real four-model system: own deep-history fetch,
// own cache files, own position/risk state, own Telegram sends via a direct (non-queued)
// TelegramNotifier built from the raw IHttpClientFactory - never the shared
// TelegramDeliveryOutbox the real flow routes through. Reads nothing from and writes nothing
// to SignalOrchestrator, SignalLogService or SignalAlertService's state.
// Fetches its own market data directly; never touches OrchestratorResult at all.
public class VtbSignalService(IHttpClientFactory httpClients, IConfiguration config, IHostEnvironment env, ILogger<VtbSignalService> logger)
{
    // VTB-local data source marker - deliberately NOT the shared Models.DataSource enum (that
    // one drives the real orchestrator's routing); VTB's data fetch is entirely its own code
    // path and has no reason to share a type with it.
    private enum VtbDataSource { Coinbase, TwelveData, Yahoo }

    private record MarketConfig(string Symbol, VtbDataSource Source, string FeedSymbol, int ChannelN, decimal RewardToRisk, decimal MarketRiskMultiplier = 1.0m);

    private static readonly MarketConfig[] Markets =
    {
        new("BTC/USDT", VtbDataSource.Coinbase, "BTC-USD", 20, 2.0m),
        new("ETH/USDT", VtbDataSource.Coinbase, "ETH-USD", 55, 2.0m),
        new("XAU/USD", VtbDataSource.TwelveData, "XAU/USD", 55, 1.5m),
        // WTI: no real edge found on 2023-26 data per the dev brief (+0.13R, not
        // distinguishable from zero) - sized at 0.25x per the brief's own recommendation
        // rather than excluded outright, same spirit as shorts being sized down rather
        // than dropped. Yahoo Finance (CL=F) rather than Dukascopy: plain HTTP+JSON, no
        // Node/subprocess dependency - matches how the gold fetch below already works.
        new("WTI/USD", VtbDataSource.Yahoo, "CL=F", 55, 1.5m, MarketRiskMultiplier: 0.25m),
    };

    public const decimal DefaultEquity = 10_000m;
    public const decimal RiskPct = 0.5m;          // % of equity per full-size long
    public const decimal ShortRiskMultiplier = 0.5m;
    public const decimal CryptoCombinedRiskCapR = 1.5m; // combined BTC+ETH open risk, in R
    public const decimal DrawdownGuardR = 12m;    // matches the Dev Prompt rev.2 reasoning: 7.4R
                                                   // by signal date, ~11R mark-to-market - 12R sits above both.

    private readonly string _ledgerPath = Path.Combine(env.ContentRootPath, ".cache", "vtb-signal-log.json");
    private readonly string _historyCachePath = Path.Combine(env.ContentRootPath, ".cache", "vtb-history-cache.json");
    private readonly string _riskStatePath = Path.Combine(env.ContentRootPath, ".cache", "vtb-risk-state.json");
    private readonly List<VtbLogEntry> _entries =
        DiskCache.Load<List<VtbLogEntry>>(Path.Combine(env.ContentRootPath, ".cache", "vtb-signal-log.json")) ?? new();
    private readonly VtbRiskState _risk =
        DiskCache.Load<VtbRiskState>(Path.Combine(env.ContentRootPath, ".cache", "vtb-risk-state.json")) ?? new();
    private readonly object _lock = new();
    // Routes to a different chat by building a small in-memory config that overrides
    // Telegram:ChatId, rather than a per-call parameter (TelegramNotifier.SendAsync no longer
    // takes one). Falls back to the real chat if a dedicated VtbChatId isn't configured.
    private readonly IConfiguration _vtbTelegramConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Telegram:BotToken"] = config["Telegram:BotToken"],
        ["Telegram:ChatId"] = string.IsNullOrWhiteSpace(config["Telegram:VtbChatId"]) ? config["Telegram:ChatId"] : config["Telegram:VtbChatId"],
    }).Build();

    private record HistoryCacheEntry(DateTime FetchedAtUtc, List<NormalizedCandle> Candles);

    private record VtbRiskState
    {
        public decimal PeakEquityR { get; set; } = 0m;
        public decimal CumulativeR { get; set; } = 0m;
        public bool DrawdownGuardTripped { get; set; } = false;
    }

    public record VtbLogEntry(
        string Id, string Symbol, string Side, DateTime EntryTimeUtc, decimal EntryRef, decimal Stop, decimal Target, decimal Risk,
        string Status, DateTime? ExitTimeUtc, decimal? Exit, string? ExitReason, decimal? R, decimal RiskMultiplier);

    public IReadOnlyList<VtbLogEntry> GetAll() { lock (_lock) return _entries.ToList(); }

    public async Task ProcessAsync()
    {
        try
        {
            foreach (var market in Markets)
            {
                IReadOnlyList<NormalizedCandle> daily;
                try { daily = await GetDailyHistoryAsync(market); }
                catch (Exception ex) { logger.LogWarning(ex, "VTB: could not fetch daily history for {Symbol}", market.Symbol); continue; }
                if (daily.Count < 220) continue; // not enough history for SMA200 + channel yet

                var (closed, open) = VtbRules.Replay(daily, market.ChannelN, market.RewardToRisk);
                await ReconcileAsync(market, closed, open);
            }
        }
        catch (Exception ex) { logger.LogError(ex, "VTB processing failed unexpectedly"); }
    }

    // Diffs the freshly-replayed (deterministic) result against the persisted ledger: any
    // closed trade not already logged gets logged + alerted; a currently-open position not
    // already logged (by entry time) gets logged + alerted, subject to the risk guards below.
    private async Task ReconcileAsync(MarketConfig market, IReadOnlyList<VtbClosedTrade> closed, VtbPosition? open)
    {
        foreach (var trade in closed)
        {
            VtbLogEntry? updated = null;
            lock (_lock)
            {
                var idx = _entries.FindIndex(e => e.Symbol == market.Symbol && e.EntryTimeUtc == trade.EntryTimeUtc && e.Status == "Open");
                if (idx < 0) continue; // this closed trade was never logged as opened (e.g. before VTB existed) - skip, don't fabricate
                var e = _entries[idx];
                var moneyR = trade.R * e.RiskMultiplier; // money-based R per spec §5 (half-size short = 0.5R on its own stop)
                updated = e with { Status = "Closed", ExitTimeUtc = trade.ExitTimeUtc, Exit = trade.Exit, ExitReason = trade.Reason.ToString(), R = moneyR };
                _entries[idx] = updated;
                _risk.CumulativeR += moneyR;
                if (_risk.CumulativeR > _risk.PeakEquityR) _risk.PeakEquityR = _risk.CumulativeR;
                if (_risk.PeakEquityR - _risk.CumulativeR < DrawdownGuardR) _risk.DrawdownGuardTripped = false;
                Persist();
            }
            if (updated is not null) await SendOutcomeAsync(updated);
        }

        if (open is null) return;
        bool alreadyLogged;
        lock (_lock) alreadyLogged = _entries.Any(e => e.Symbol == market.Symbol && e.EntryTimeUtc == open.EntryTimeUtc);
        if (alreadyLogged) return;

        VtbLogEntry opened;
        lock (_lock)
        {
            if (_risk.DrawdownGuardTripped) return;
            if (_risk.PeakEquityR - _risk.CumulativeR >= DrawdownGuardR)
            {
                _risk.DrawdownGuardTripped = true;
                Persist();
                logger.LogWarning("VTB drawdown guard tripped ({DD}R from peak) - no new VTB trades until manually reset", _risk.PeakEquityR - _risk.CumulativeR);
                return;
            }

            var isShort = open.Side == VtbSide.Short;
            // Compounds: a half-size short on WTI (0.25x market multiplier) sizes at 0.125x
            // overall, per the brief's position-sizing formula (risk_pct x side_mult x market_mult).
            var riskMultiplier = (isShort ? ShortRiskMultiplier : 1.0m) * market.MarketRiskMultiplier;

            // Crypto combined-risk cap: BTC + ETH open risk (in R) must not exceed 1.5R.
            if (market.Symbol is "BTC/USDT" or "ETH/USDT")
            {
                var openCryptoR = _entries.Where(e => e.Status == "Open" && e.Symbol is "BTC/USDT" or "ETH/USDT").Sum(e => e.RiskMultiplier);
                if (openCryptoR + riskMultiplier > CryptoCombinedRiskCapR)
                {
                    logger.LogInformation("VTB: skipped {Symbol} {Side} - would breach the 1.5R combined BTC+ETH risk cap", market.Symbol, open.Side);
                    return;
                }
            }

            opened = new VtbLogEntry(Guid.NewGuid().ToString("N"), market.Symbol, open.Side.ToString(), open.EntryTimeUtc,
                open.EntryRef, open.Stop, open.Target, open.Risk, "Open", null, null, null, null, riskMultiplier);
            _entries.Add(opened);
            Persist();
        }
        await SendOpenAsync(opened, market);
    }

    private void Persist()
    {
        DiskCache.Save(_ledgerPath, _entries);
        DiskCache.Save(_riskStatePath, _risk);
    }

    // ---- data: deep daily history, cached once a day (VTB only ever evaluates the daily close) ----
    private async Task<IReadOnlyList<NormalizedCandle>> GetDailyHistoryAsync(MarketConfig market)
    {
        var cacheAll = DiskCache.Load<Dictionary<string, HistoryCacheEntry>>(_historyCachePath) ?? new();
        if (cacheAll.TryGetValue(market.Symbol, out var cached) && DateTime.UtcNow - cached.FetchedAtUtc < TimeSpan.FromHours(20))
            return cached.Candles;

        List<NormalizedCandle> candles = market.Source switch
        {
            VtbDataSource.Coinbase => await CoinbaseHistory.FetchAsync(httpClients.CreateClient(), market.Symbol, market.FeedSymbol, Timeframe.Daily, 86400,
                DateTime.UtcNow.AddDays(-620), DateTime.UtcNow, CancellationToken.None),
            VtbDataSource.Yahoo => await FetchYahooDailyAsync(market.FeedSymbol),
            _ => await FetchTwelveDataDailyAsync(market.FeedSymbol),
        };

        cacheAll[market.Symbol] = new HistoryCacheEntry(DateTime.UtcNow, candles);
        DiskCache.Save(_historyCachePath, cacheAll);
        return candles;
    }

    // Deep gold history: the shared TwelveDataMarketDataProvider is hardcoded to the last 120
    // bars (fine for the real system's needs, not enough for a 200-day SMA) - this is a
    // separate, VTB-only fetch requesting real depth. Uses the first configured key only (no
    // rotation): called at most once a day thanks to the cache above, so quota pressure is low.
    private async Task<List<NormalizedCandle>> FetchTwelveDataDailyAsync(string providerSymbol)
    {
        var keys = ApiKeys.Get(config, "TwelveData");
        if (keys.Count == 0) throw new InvalidOperationException("VTB: Twelve Data API key not configured.");
        var client = httpClients.CreateClient();
        var url = $"https://api.twelvedata.com/time_series?symbol={Uri.EscapeDataString(providerSymbol)}&interval=1day&outputsize=800&timezone=UTC&apikey={keys[0]}";
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("values", out var values))
            throw new InvalidOperationException($"VTB: Twelve Data gold fetch failed: {text[..Math.Min(200, text.Length)]}");

        var list = new List<NormalizedCandle>();
        foreach (var v in values.EnumerateArray())
        {
            var open = DateTime.Parse(v.GetProperty("datetime").GetString()!).ToUniversalTime();
            list.Add(new NormalizedCandle("XAU/USD", providerSymbol, "TwelveData", Timeframe.Daily, open, open.AddDays(1),
                decimal.Parse(v.GetProperty("open").GetString()!), decimal.Parse(v.GetProperty("high").GetString()!),
                decimal.Parse(v.GetProperty("low").GetString()!), decimal.Parse(v.GetProperty("close").GetString()!),
                null, true, DateTime.UtcNow, DataQuality.Ok));
        }
        list.Sort((a, b) => a.OpenTimeUtc.CompareTo(b.OpenTimeUtc));
        return list;
    }

    // WTI history: no Coinbase/Twelve Data coverage for crude oil, so this hits Yahoo
    // Finance's public chart endpoint directly - a plain unauthenticated JSON fetch, no
    // API key. Parallel arrays (timestamp / open / high / low / close) indexed together;
    // Yahoo returns null in any of the OHLC arrays for a few non-trading/partial bars, so
    // those indices are skipped rather than producing a zero-range candle.
    private async Task<List<NormalizedCandle>> FetchYahooDailyAsync(string tickerSymbol)
    {
        var client = httpClients.CreateClient();
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(tickerSymbol)}?interval=1d&range=5y";
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var result = doc.RootElement.GetProperty("chart").GetProperty("result");
        if (result.ValueKind != System.Text.Json.JsonValueKind.Array || result.GetArrayLength() == 0)
            throw new InvalidOperationException($"VTB: Yahoo WTI fetch failed: {text[..Math.Min(200, text.Length)]}");

        var root = result[0];
        var timestamps = root.GetProperty("timestamp");
        var quote = root.GetProperty("indicators").GetProperty("quote")[0];
        var opens = quote.GetProperty("open");
        var highs = quote.GetProperty("high");
        var lows = quote.GetProperty("low");
        var closes = quote.GetProperty("close");

        var list = new List<NormalizedCandle>();
        for (var i = 0; i < timestamps.GetArrayLength(); i++)
        {
            if (opens[i].ValueKind == System.Text.Json.JsonValueKind.Null || highs[i].ValueKind == System.Text.Json.JsonValueKind.Null ||
                lows[i].ValueKind == System.Text.Json.JsonValueKind.Null || closes[i].ValueKind == System.Text.Json.JsonValueKind.Null)
                continue;
            var open = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime;
            list.Add(new NormalizedCandle("WTI/USD", tickerSymbol, "Yahoo", Timeframe.Daily, open, open.AddDays(1),
                Convert.ToDecimal(opens[i].GetDouble()), Convert.ToDecimal(highs[i].GetDouble()),
                Convert.ToDecimal(lows[i].GetDouble()), Convert.ToDecimal(closes[i].GetDouble()),
                null, true, DateTime.UtcNow, DataQuality.Ok));
        }
        list.Sort((a, b) => a.OpenTimeUtc.CompareTo(b.OpenTimeUtc));
        return list;
    }

    // ---- Telegram: direct send, independent of the real flow's TelegramDeliveryOutbox ----
    private async Task SendOpenAsync(VtbLogEntry e, MarketConfig market)
    {
        var telegram = new TelegramNotifier(httpClients, _vtbTelegramConfig);
        var icon = e.Side == "Long" ? "🟢" : "🔴";
        var text = $"📈 *VTB* · {e.Symbol} · {icon} *{e.Side.ToUpperInvariant()}*\n" +
            $"Channel {market.ChannelN}d · R:R 1:{market.RewardToRisk}\n\n" +
            $"🎯 Entry {e.EntryRef:0.####}\n🛑 Stop {e.Stop:0.####}\n🏁 Target {e.Target:0.####}\n" +
            $"⏱ Time exit in {VtbRules.TimeExitDays} days\n" +
            (e.RiskMultiplier < 1m ? $"\n⚠️ Short - sized at {e.RiskMultiplier:0.0}x risk" : "") +
            "\n\nPaper trade - backtested rules, not a live order.";
        var (success, error) = await telegram.SendAsync(text);
        if (!success) logger.LogWarning("VTB open Telegram alert failed for {Symbol}: {Error}", e.Symbol, error);
    }

    private async Task SendOutcomeAsync(VtbLogEntry e)
    {
        var telegram = new TelegramNotifier(httpClients, _vtbTelegramConfig);
        var label = (e.R ?? 0m) >= 0 ? "WIN" : "LOSS";
        var text = $"📈 *VTB OUTCOME* · {e.Symbol} · {e.Side} → {label}\nNet {e.R:+0.00;-0.00}R · {e.ExitReason}";
        var (success, error) = await telegram.SendAsync(text);
        if (!success) logger.LogWarning("VTB outcome Telegram alert failed for {Symbol}: {Error}", e.Symbol, error);
    }
}
