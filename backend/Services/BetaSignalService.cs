using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// Paper-trades the five shadow-mode strategies (ShadowStrategies.cs) for real, with their
// own Telegram alerts - but kept entirely separate from the real ledger and real alerts so
// neither can interfere with the other:
//   - Its own persisted state (.cache/beta-signal-log.json), own one-open-trade-per
//     (symbol, timeframe, model) dedup regardless of direction - same rule the real
//     ledger uses, so one model can't hold a Long and a Short open at once on the same
//     symbol+timeframe, which is incoherent (it can't genuinely believe both at the
//     same time). Own TradeSimulator-driven tracking.
//   - Its own Telegram messages, clearly marked BETA, sent independently of
//     SignalAlertService's dedup/pause rules.
//   - Its entries are tagged Grade="Beta" and only MERGED into the ledger JSON the
//     frontend reads (see Program.cs) at output time - SignalLogService's own state and
//     its one-open-trade-per-symbol+timeframe invariant (which assumes one real signal
//     per slot) is never touched or shared with this.
// These are unvalidated, experimental models (see STRATEGY.md promotion gate) - this is
// their live forward-test, not a claim that they are trading-quality yet.
public class BetaSignalService(TelegramNotifier telegram, IHostEnvironment env, IConfiguration config, ILogger<BetaSignalService> logger)
{
    // Optional: posts to a separate chat/group than the main signals, so beta trades
    // never mix into the real channel. Falls back to the default Telegram:ChatId (the
    // same channel as everything else) if this isn't set.
    private readonly string? _betaChatId = config["Telegram:BetaChatId"];
    private readonly string _path = Path.Combine(env.ContentRootPath, ".cache", "beta-signal-log.json");
    private readonly List<QualificationLogEntry> _entries =
        DiskCache.Load<List<QualificationLogEntry>>(Path.Combine(env.ContentRootPath, ".cache", "beta-signal-log.json")) ?? new();
    private readonly object _lock = new();

    // Deliberately NOT keyed by direction: one model can't coherently hold a Long and a
    // Short open at once on the same symbol+timeframe. A reversal candidate while one is
    // already open is suppressed, same as the real ledger's rule - see ProcessAsync.
    private static string Key(string symbol, string timeframe, ShadowModel model) => $"{symbol}|{timeframe}|{model}";

    public async Task ProcessAsync(IEnumerable<OrchestratorResult> results)
    {
        try
        {
            foreach (var result in results)
            {
                if (result.Candles is null) continue;

                // 1. Update every currently-open beta trade for this symbol+timeframe against
                // the fresh candles, same simulator the real ledger uses.
                List<QualificationLogEntry> justClosed;
                lock (_lock)
                {
                    justClosed = new List<QualificationLogEntry>();
                    for (var i = 0; i < _entries.Count; i++)
                    {
                        var e = _entries[i];
                        if (e.Symbol != result.InstrumentSymbol || e.Timeframe != result.Timeframe || !TradeSimulator.IsOpen(e.Status)) continue;
                        var updated = TradeSimulator.ApplyCandleSequence(e,
                            result.Candles.Where(c => c.OpenTimeUtc >= e.QualifiedAtUtc).ToList(), DateTime.UtcNow);
                        _entries[i] = updated;
                        if (!TradeSimulator.IsOpen(updated.Status)) justClosed.Add(updated);
                    }
                    if (justClosed.Count > 0) Persist();
                }
                foreach (var closed in justClosed) await SendOutcomeAsync(closed);

                // 2. Open a beta trade for any fresh candidate that doesn't already have one
                // open for the same (symbol, timeframe, model) - in EITHER direction (see Key).
                // Held to the same two symbol-wide gates the real four models use: a live hard
                // calendar veto blocks every candidate here outright, and a candidate whose
                // direction conflicts with the Weekly/Daily HTF read is skipped too - these
                // models' own checklists are lighter (single-timeframe), so this is what brings
                // them up to the real system's risk floor rather than lowering it.
                if (result.ShadowCandidates is not { Count: > 0 }) continue;
                if (result.ShadowCalendarVeto == CalendarVetoState.HardVeto) continue;
                // CAKE/USDT dropped from beta: low liquidity, worst backtest result of any
                // symbol by far (-67R to -70R net over 180 days across models). Real CAKE/USDT
                // signals on the live system are unaffected - this only stops NEW beta trades;
                // anything already open on CAKE still gets tracked to a real close above.
                if (result.InstrumentSymbol == "CAKE/USDT") continue;
                foreach (var candidate in result.ShadowCandidates)
                {
                    if (result.ShadowHtf is { } htf && htf.TryGetValue(candidate.Direction, out var alignment) && alignment == HtfAlignment.Conflicting)
                        continue;

                    var key = Key(result.InstrumentSymbol, result.Timeframe, candidate.Model);
                    bool alreadyOpen;
                    QualificationLogEntry opened;
                    lock (_lock)
                    {
                        alreadyOpen = _entries.Any(e => TradeSimulator.IsOpen(e.Status) &&
                            Key(e.Symbol, e.Timeframe, Enum.Parse<ShadowModel>(e.SetupModel)) == key);
                        if (alreadyOpen) continue;
                        opened = OpenEntry(result.InstrumentSymbol, result.Timeframe, candidate);
                        _entries.Add(opened);
                        Persist();
                    }
                    await SendOpenAsync(opened, candidate);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Beta signal processing failed unexpectedly");
        }
    }

    public IReadOnlyList<QualificationLogEntry> GetAll()
    {
        lock (_lock) return _entries.ToList();
    }

    // Same bands as the live confluence scorer (Strategy/ConfluenceScore.cs) - a beta
    // entry's score means the same thing a real one's does, so it earns the same letter.
    // Beta-ness itself is signalled by which ledger/dashboard section the entry lives in,
    // not by the grade - these are now kept in a separate output entirely (see
    // Program.cs's beta-log.json), not merged into the real signal-log.json.
    private static string GradeForScore(int score) => score switch
    {
        >= 90 => "A+",
        >= 85 => "A",
        >= 75 => "B",
        >= 65 => "Tracking",
        _ => "No setup"
    };

    private static QualificationLogEntry OpenEntry(string symbol, string timeframeLabel, ShadowCandidate c)
    {
        var tf = TimeframeIntervals.ParseLabel(timeframeLabel) ?? Timeframe.H1;
        InstrumentMetadataCatalog.TryGet(symbol, out var meta);
        var risk = Math.Abs(c.Entry - c.Stop);
        var now = DateTime.UtcNow;
        return new QualificationLogEntry(
            Id: Guid.NewGuid().ToString("N"), Symbol: symbol, Timeframe: timeframeLabel, Direction: c.Direction.ToString(),
            SetupModel: c.Model.ToString(), Grade: GradeForScore(c.Score), Score: c.Score, Entry: c.Entry, Stop: c.Stop, Tp1: c.Tp1, Tp2: c.Tp2, Tp3: c.Tp3,
            RewardToRisk: 2m, QualifiedAtUtc: now, TrackingExpiryUtc: now + TimeframeConfig.Duration(tf) * 30,
            Status: "Open", Tp1HitAtUtc: null, Tp2HitAtUtc: null, ClosedAtUtc: null, RealizedR: null,
            AssetClass: meta?.AssetClass ?? "", FinalStop: c.Stop, RiskPercent: 1m, RiskAmount: 100m,
            PositionSize: risk > 0 ? 100m / risk : 0m,
            EntrySpreadUnits: meta?.DefaultSpreadUnits ?? 0m, SlippageUnits: meta?.DefaultSlippageUnits ?? 0m,
            Exits: Array.Empty<ExitFill>(), CommissionUnits: meta?.CommissionUnits ?? 0m, EntryFillPrice: c.Entry, EntryTimeUtc: now);
    }

    private void Persist() => DiskCache.Save(_path, _entries);

    // Telegram rate-limits messages to the SAME chat to roughly 1/second; a scan that
    // opens/closes many beta trades at once was firing them all with zero delay, and a
    // throttled (429) send just logged a warning server-side and was silently dropped -
    // never retried. Real symptom: opens went through, outcomes for trades that actually
    // hit stop/TP sometimes never did. Paced sends fix this; semaphore (not a lock) since
    // the wait itself is async.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private DateTime _lastSendUtc = DateTime.MinValue;
    private static readonly TimeSpan MinSendGap = TimeSpan.FromMilliseconds(1100);

    private async Task<(bool Success, string? Error)> PacedSendAsync(string text)
    {
        await _sendGate.WaitAsync();
        try
        {
            var wait = MinSendGap - (DateTime.UtcNow - _lastSendUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            var result = await telegram.SendAsync(text, chatIdOverride: _betaChatId);
            _lastSendUtc = DateTime.UtcNow;
            return result;
        }
        finally { _sendGate.Release(); }
    }

    private async Task SendOpenAsync(QualificationLogEntry e, ShadowCandidate c)
    {
        var directionIcon = e.Direction == "Long" ? "🟢" : "🔴";
        var text =
            $"🧪 *BETA* · {e.Symbol} · {e.Timeframe} · {directionIcon} *{e.Direction.ToUpperInvariant()}*\n" +
            $"{e.SetupModel} · Grade {e.Grade} ({e.Score})\n\n" +
            $"🎯 Entry {TelegramSignalFormatter.FormatPrice(e.Entry)}\n" +
            $"🛑 Stop {TelegramSignalFormatter.FormatPrice(e.Stop)}\n" +
            $"🏁 TP1 {TelegramSignalFormatter.FormatPrice(e.Tp1)} · TP2 {TelegramSignalFormatter.FormatPrice(e.Tp2)} · TP3 {TelegramSignalFormatter.FormatPrice(e.Tp3)}\n\n" +
            (c.Evidence.Count > 0 ? string.Join("\n", c.Evidence.Select(ev => $"• {ev}")) : "");
        var (success, error) = await PacedSendAsync(text);
        if (!success) logger.LogWarning("Beta signal Telegram alert failed for {Symbol} {Timeframe}: {Error}", e.Symbol, e.Timeframe, error);
    }

    private async Task SendOutcomeAsync(QualificationLogEntry e)
    {
        var label = (e.NetRealizedR ?? e.RealizedR ?? 0m) >= 0 ? "WIN" : "LOSS";
        var text = $"🧪 *BETA OUTCOME* · {e.Symbol} · {e.Timeframe} · {e.SetupModel} → {label}\n" +
            $"Net {e.NetRealizedR ?? e.RealizedR:+0.00;-0.00}R · {e.FinalOutcome ?? e.Status}";
        var (success, error) = await PacedSendAsync(text);
        if (!success) logger.LogWarning("Beta outcome Telegram alert failed for {Symbol} {Timeframe}: {Error}", e.Symbol, e.Timeframe, error);
    }
}
