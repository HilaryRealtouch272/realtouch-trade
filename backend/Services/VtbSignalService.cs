using System.Globalization;
using RealtouchSmartTrade.Api.Backtesting;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// Validated Trend Breakout (VTB) - a daily SMA200-trend + Donchian-channel-breakout module,
// paper-traded for BTC/USDT, ETH/USDT, XAU/USD and WTI/USD.
//
// COMPLETELY ISOLATED from the real four-model system: own deep-history fetch,
// own cache files, own position/risk state, and its own Telegram delivery queue.
// The real flow's TelegramDeliveryOutbox instance and state are never shared.
// Reads nothing from and writes nothing
// to SignalOrchestrator, SignalLogService or SignalAlertService's state.
// Fetches its own market data directly; never touches OrchestratorResult at all.
public class VtbSignalService(IHttpClientFactory httpClients, IConfiguration config, IHostEnvironment env, ILogger<VtbSignalService> logger,
    Func<DateTime>? clock = null, AutoTradingSignalSink? autoTrading = null)
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
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private DateTime UtcNow => clock?.Invoke() ?? DateTime.UtcNow;
    private TelegramDeliveryOutbox? _delivery;
    private TelegramDeliveryOutbox Delivery => _delivery ??= new TelegramDeliveryOutbox(
        httpClients, _vtbTelegramConfig, env, logger, stateFileName: "vtb-telegram-delivery.json",
        canDeliver: id => id.EndsWith(":open", StringComparison.Ordinal)
            ? CanDeliverOpen(id)
            : id.EndsWith(":outcome", StringComparison.Ordinal) &&
                Delivery.Get(id[..^8] + ":open")?.Status == "Delivered");
    // Routes to a different chat by building a small in-memory config that overrides
    // Telegram:ChatId, rather than a per-call parameter (TelegramNotifier.SendAsync no longer
    // takes one). Falls back to the real chat if a dedicated VtbChatId isn't configured.
    private readonly IConfiguration _vtbTelegramConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Telegram:BotToken"] = config["Telegram:BotToken"],
        ["Telegram:ChatId"] = string.IsNullOrWhiteSpace(config["Telegram:VtbChatId"]) ? config["Telegram:ChatId"] : config["Telegram:VtbChatId"],
    }).Build();

    private bool CanDeliverOpen(string id)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id + ":open" == id);
            return entry is { Status: "Open" } && MarketHours.NewEntriesAllowed(entry.Symbol, UtcNow)
                && (entry.ExitMode != SpxTrendRules.ExitMode ||
                    (entry.ConfirmedAtUtc is DateTime confirmed && UtcNow >= confirmed && UtcNow - confirmed <= TimeSpan.FromMinutes(45)));
        }
    }

    private record HistoryCacheEntry(DateTime FetchedAtUtc, List<NormalizedCandle> Candles);

    private record VtbRiskState
    {
        public decimal PeakEquityR { get; set; } = 0m;
        public decimal CumulativeR { get; set; } = 0m;
        public bool DrawdownGuardTripped { get; set; } = false;
    }

    public record VtbLogEntry(
        string Id, string Symbol, string Side, DateTime EntryTimeUtc, decimal EntryRef, decimal Stop, decimal Target, decimal Risk,
        string Status, DateTime? ExitTimeUtc, decimal? Exit, string? ExitReason, decimal? R, decimal RiskMultiplier,
        bool OpenAlertPending = false, bool OutcomeAlertPending = false,
        string? ExitMode = null, DateTime? ConfirmedAtUtc = null);

    public IReadOnlyList<VtbLogEntry> GetAll() { lock (_lock) return _entries.ToList(); }

    public async Task ProcessAsync()
    {
        await _processGate.WaitAsync();
        try
        {
            if (!MarketHours.NewEntriesAllowed(UtcNow))
                logger.LogInformation("VTB Sunday pause (Lagos): tracking existing positions and outcomes");
            foreach (var market in Markets)
            {
                try
                {
                    var daily = await GetDailyHistoryAsync(market);
                    var now = UtcNow;
                    SettleOpenPositions(market, daily, now);
                    var complete = daily.Where(c => c.IsComplete && c.CloseTimeUtc <= now).ToList();
                    if (complete.Count < 220) continue;
                    var (_, open) = VtbRules.Replay(complete, market.ChannelN, market.RewardToRisk);
                    ReconcileOpen(market, open);
                    SettleOpenPositions(market, daily, now);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "VTB: could not process {Symbol}", market.Symbol);
                }
            }
            await ProcessSpxAsync();
            await SendPendingAlertsAsync();
            await Delivery.FlushAsync();
        }
        catch (Exception ex) { logger.LogError(ex, "VTB processing failed unexpectedly"); }
        finally { _processGate.Release(); }
    }

    private void SettleOpenPositions(MarketConfig market, IReadOnlyList<NormalizedCandle> daily, DateTime now)
    {
        lock (_lock)
        {
            for (var idx = 0; idx < _entries.Count; idx++)
            {
                var e = _entries[idx];
                if (e.Symbol != market.Symbol || e.Status != "Open") continue;
                var position = new VtbPosition(Enum.Parse<VtbSide>(e.Side), e.EntryTimeUtc, e.EntryRef, e.Risk, e.Stop, e.Target);
                var trade = VtbRules.Settle(position, daily, now);
                if (trade is null) continue;
                var moneyR = trade.R * e.RiskMultiplier;
                _entries[idx] = e with { Status = "Closed", ExitTimeUtc = trade.ExitTimeUtc, Exit = trade.Exit,
                    ExitReason = trade.Reason.ToString(), R = moneyR, OpenAlertPending = false, OutcomeAlertPending = true };
                _risk.CumulativeR += moneyR;
                if (_risk.CumulativeR > _risk.PeakEquityR) _risk.PeakEquityR = _risk.CumulativeR;
                if (_risk.PeakEquityR - _risk.CumulativeR < DrawdownGuardR) _risk.DrawdownGuardTripped = false;
                Persist();
                Delivery.CancelPending(e.Id + ":open");
            }
        }
    }

    private void ReconcileOpen(MarketConfig market, VtbPosition? open)
    {
        if (open is null || !MarketHours.NewEntriesAllowed(market.Symbol, UtcNow)) return;
        bool alreadyLogged;
        lock (_lock) alreadyLogged = _entries.Any(e => e.Symbol == market.Symbol && (e.EntryTimeUtc == open.EntryTimeUtc || e.Status == "Open"));
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
                open.EntryRef, open.Stop, open.Target, open.Risk, "Open", null, null, null, null, riskMultiplier, OpenAlertPending: true);
            _entries.Add(opened);
            Persist();
        }
    }

    private void Persist()
    {
        DiskCache.Save(_ledgerPath, _entries);
        DiskCache.Save(_riskStatePath, _risk);
    }

    private async Task ProcessSpxAsync()
    {
        try
        {
            var market = new MarketConfig(SpxTrendRules.Symbol, VtbDataSource.Yahoo, "^GSPC", 55, 0m);
            var daily = await GetDailyHistoryAsync(market);
            var now = UtcNow;
            lock (_lock)
            {
                for (var i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (e.Symbol != SpxTrendRules.Symbol || e.Status != "Open") continue;
                    var trade = SpxTrendRules.Settle(new(VtbSide.Long, e.EntryTimeUtc, e.EntryRef, e.Risk, e.Stop, 0m), daily, now);
                    if (trade is null) continue;
                    var moneyR = trade.R * e.RiskMultiplier;
                    _entries[i] = e with { Status = "Closed", ExitTimeUtc = trade.ExitTimeUtc, Exit = trade.Exit,
                        ExitReason = trade.Reason.ToString(), R = moneyR, OpenAlertPending = false, OutcomeAlertPending = true };
                    _risk.CumulativeR += moneyR;
                    _risk.PeakEquityR = Math.Max(_risk.PeakEquityR, _risk.CumulativeR);
                    Persist();
                    Delivery.CancelPending(e.Id + ":open");
                }
                var complete = daily.Where(c => c.IsComplete && c.CloseTimeUtc <= now).ToList();
                if (complete.Count < 220) return;
                var last = complete[^1];
                // Do not resurrect old research positions or Monday-alert Friday's setup.
                if (!SpxTrendRules.FreshEntry(last, now)) return;
                if (VtbRules.SignalAt(VtbRules.ComputeIndicators(complete, 55)[^1]) != VtbSide.Long) return;
                if (_entries.Any(e => e.Symbol == market.Symbol && (e.Status == "Open" || e.EntryTimeUtc == last.OpenTimeUtc))) return;
                // Admission guard for this new sleeve; existing four-market behavior stays intact.
                if (_entries.Count(e => e.Status == "Open") >= 4 || _risk.DrawdownGuardTripped
                    || _risk.PeakEquityR - _risk.CumulativeR >= DrawdownGuardR) return;
                var risk = VtbRules.ComputeIndicators(complete, 55)[^1].Atr14 * 1.5m;
                if (risk <= 0m) return;
                _entries.Add(new(Guid.NewGuid().ToString("N"), market.Symbol, "Long", last.OpenTimeUtc,
                    last.Close, last.Close - risk, 0m, risk, "Open", null, null, null, null, 1m,
                    OpenAlertPending: true, ExitMode: SpxTrendRules.ExitMode, ConfirmedAtUtc: last.CloseTimeUtc));
                Persist();
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "SPX trend: could not process daily history"); }
    }

    // Refresh the deep indicator history once per UTC day and its recent tail every scan.
    // Developing bars settle saved positions but never confirm new daily entries.
    private async Task<IReadOnlyList<NormalizedCandle>> GetDailyHistoryAsync(MarketConfig market)
    {
        var now = UtcNow;
        var cacheAll = DiskCache.Load<Dictionary<string, HistoryCacheEntry>>(_historyCachePath) ?? new();
        cacheAll.TryGetValue(market.Symbol, out var cached);
        var warm = cached is not null && cached.FetchedAtUtc.Date == now.Date;
        var from = warm ? now.Date.AddDays(-30) : now.Date.AddDays(-620);
        List<NormalizedCandle> candles = market.Source switch
        {
            VtbDataSource.Coinbase => await CoinbaseHistory.FetchAsync(httpClients.CreateClient(), market.Symbol,
                market.FeedSymbol, Timeframe.Daily, 86400, from, now.Date.AddDays(1), CancellationToken.None),
            VtbDataSource.Yahoo => await FetchYahooDailyAsync(market.FeedSymbol, warm ? "1mo" : "5y", market.Symbol),
            _ => await FetchTwelveDataDailyAsync(market.FeedSymbol, warm ? 30 : 800),
        };
        if (candles.Count == 0) throw new InvalidOperationException("VTB: empty daily history for " + market.Symbol);
        var merged = (warm ? cached!.Candles.Concat(candles) : candles)
            .GroupBy(c => c.OpenTimeUtc).Select(g => g.Last())
            .OrderBy(c => c.OpenTimeUtc)
            .Select(c => c with { IsComplete = c.CloseTimeUtc <= now }).ToList();
        cacheAll[market.Symbol] = new HistoryCacheEntry(warm ? cached!.FetchedAtUtc : now, merged);
        DiskCache.Save(_historyCachePath, cacheAll);
        return merged;
    }

    // Deep gold history: the shared TwelveDataMarketDataProvider is hardcoded to the last 120
    // bars (fine for the real system's needs, not enough for a 200-day SMA) - this is a
    // separate, VTB-only fetch requesting real depth once per day and 30 recent bars per scan.
    // Uses the first configured key only.
    private async Task<List<NormalizedCandle>> FetchTwelveDataDailyAsync(string providerSymbol, int outputSize)
    {
        var keys = ApiKeys.Get(config, "TwelveData");
        if (keys.Count == 0) throw new InvalidOperationException("VTB: Twelve Data API key not configured.");
        var client = httpClients.CreateClient();
        var url = $"https://api.twelvedata.com/time_series?symbol={Uri.EscapeDataString(providerSymbol)}&interval=1day&outputsize={outputSize}&timezone=UTC&apikey={keys[0]}";
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("values", out var values))
            throw new InvalidOperationException($"VTB: Twelve Data gold fetch failed: {text[..Math.Min(200, text.Length)]}");

        var list = new List<NormalizedCandle>();
        foreach (var v in values.EnumerateArray())
        {
            var open = DateTime.Parse(v.GetProperty("datetime").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            list.Add(new NormalizedCandle("XAU/USD", providerSymbol, "TwelveData", Timeframe.Daily, open, open.AddDays(1),
                decimal.Parse(v.GetProperty("open").GetString()!, CultureInfo.InvariantCulture), decimal.Parse(v.GetProperty("high").GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(v.GetProperty("low").GetString()!, CultureInfo.InvariantCulture), decimal.Parse(v.GetProperty("close").GetString()!, CultureInfo.InvariantCulture),
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
    private async Task<List<NormalizedCandle>> FetchYahooDailyAsync(string tickerSymbol, string range, string symbol = "WTI/USD")
    {
        var client = httpClients.CreateClient();
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(tickerSymbol)}?interval=1d&range={range}";
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
            var close = open.AddDays(1);
            if (symbol == SpxTrendRules.Symbol)
            {
                open = DateTime.SpecifyKind(SpxTrendRules.SessionDate(open), DateTimeKind.Utc);
                if (open.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                close = SpxTrendRules.SessionClose(open);
                if (highs[i].GetDouble() <= lows[i].GetDouble()) continue;
            }
            list.Add(new NormalizedCandle(symbol, tickerSymbol, "Yahoo", Timeframe.Daily, open, close,
                Convert.ToDecimal(opens[i].GetDouble()), Convert.ToDecimal(highs[i].GetDouble()),
                Convert.ToDecimal(lows[i].GetDouble()), Convert.ToDecimal(closes[i].GetDouble()),
                null, true, DateTime.UtcNow, DataQuality.Ok));
        }
        list.Sort((a, b) => a.OpenTimeUtc.CompareTo(b.OpenTimeUtc));
        return list;
    }

    private async Task SendPendingAlertsAsync()
    {
        List<VtbLogEntry> pending;
        lock (_lock) pending = _entries.Where(e => e.OpenAlertPending || e.OutcomeAlertPending).ToList();
        foreach (var entry in pending)
        {
            var openQueued = entry.OpenAlertPending && MarketHours.NewEntriesAllowed(entry.Symbol, UtcNow)
                && CanDeliverOpen(entry.Id + ":open")
                && await SendOpenAsync(entry, entry.Symbol == SpxTrendRules.Symbol
                    ? new MarketConfig(SpxTrendRules.Symbol, VtbDataSource.Yahoo, "^GSPC", 55, 0m)
                    : Markets.Single(m => m.Symbol == entry.Symbol));
            var outcomeQueued = entry.OutcomeAlertPending && await SendOutcomeAsync(entry);
            lock (_lock)
            {
                var index = _entries.FindIndex(e => e.Id == entry.Id);
                _entries[index] = _entries[index] with
                {
                    OpenAlertPending = entry.OpenAlertPending && !openQueued,
                    OutcomeAlertPending = entry.OutcomeAlertPending && !outcomeQueued
                };
                Persist();
            }
        }
    }

    // ---- Telegram: VTB-only durable queue, independent of the real flow ----
    private async Task<bool> SendOpenAsync(VtbLogEntry e, MarketConfig market)
    {
        var telegram = new TelegramNotifier(Delivery, _vtbTelegramConfig);
        var icon = e.Side == "Long" ? "🟢" : "🔴";
        var text = e.ExitMode == SpxTrendRules.ExitMode
            ? $"📈 *VTB · SPX TREND* · {icon} *LONG*\n55-day breakout · Daily close confirmed\n\n" +
              $"🎯 Entry {e.EntryRef:0.####}\n🛑 Stop {e.Stop:0.####}\n" +
              "🏁 No fixed target: exit on a daily close below the prior 50-day low.\n" +
              "⏱ No time expiry. Cash-index reference; CFD broker prices may differ.\n\nVTB trade signal."
            : $"📈 *VTB* · {e.Symbol} · {icon} *{e.Side.ToUpperInvariant()}*\n" +
            $"Channel {market.ChannelN}d · R:R 1:{market.RewardToRisk}\n\n" +
            $"🎯 Entry {e.EntryRef:0.####}\n🛑 Stop {e.Stop:0.####}\n🏁 Target {e.Target:0.####}\n" +
            $"⏱ Time exit in {VtbRules.TimeExitDays} days\n" +
            (e.RiskMultiplier < 1m ? $"\n⚠️ Short - sized at {e.RiskMultiplier:0.0}x risk" : "") +
            "\n\nVTB trade signal.";
        var (success, error) = await telegram.SendAsync(text, notificationId: e.Id + ":open");
        if (success && e.ExitMode != SpxTrendRules.ExitMode) autoTrading?.Vtb(e, e.Id + ":open");
        if (!success) logger.LogWarning("VTB open Telegram alert failed for {Symbol}: {Error}", e.Symbol, error);
        return success;
    }

    public TelegramDeliveryReceipt? GetTelegramReceipt(string? id) => Delivery.Get(id);

    private async Task<bool> SendOutcomeAsync(VtbLogEntry e)
    {
        // Ledger tracking is independent of Telegram. A TP/stop must never be
        // the first notification the owner receives for an unannounced trade.
        if (Delivery.Get(e.Id + ":open")?.Status != "Delivered") return true;
        var telegram = new TelegramNotifier(Delivery, _vtbTelegramConfig);
        var label = (e.R ?? 0m) >= 0 ? "WIN" : "LOSS";
        var text = $"📈 *VTB OUTCOME* · {e.Symbol} · {e.Side} → {label}\nNet {e.R:+0.00;-0.00}R · {e.ExitReason}";
        var (success, error) = await telegram.SendAsync(text, notificationId: e.Id + ":outcome");
        if (!success) logger.LogWarning("VTB outcome Telegram alert failed for {Symbol}: {Error}", e.Symbol, error);
        return success;
    }
}
