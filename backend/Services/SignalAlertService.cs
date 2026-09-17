using RealtouchSmartTrade.Api.Providers;

namespace RealtouchSmartTrade.Api.Services;

// Section-19-gated automatic alerting: whenever a real scan finds a setup
// that clears the grade-B qualification bar, send it to Telegram - exactly
// once per distinct qualification, not once per poll. Deliberately NOT a
// background timer of its own: it piggybacks on the SAME scans the frontend
// already triggers via /api/signals/crypto and /api/signals/fx (see
// Program.cs), so alerting costs zero extra API calls/quota beyond what's
// already being spent to drive the dashboard. The tradeoff, stated plainly:
// alerts only fire while something is actively polling those endpoints (the
// running frontend) - there is no always-on server-side scan independent of
// that yet.
public class SignalAlertService(TelegramNotifier telegram, IHostEnvironment env, ILogger<SignalAlertService> logger)
{
    // What "the same setup" means for dedup purposes: same symbol, same
    // timeframe, same direction, same setup model, same grade. If any of
    // those change while still qualified (e.g. it flips from Long to Short,
    // or B to A+), that's a materially different call worth re-alerting: it
    // is not a duplicate of the last message.
    private record AlertedState(string Direction, string SetupModel, string Grade);

    private readonly string _statePath = Path.Combine(env.ContentRootPath, ".cache", "alert-state.json");
    private readonly Dictionary<string, AlertedState> _lastAlerted =
        DiskCache.Load<Dictionary<string, AlertedState>>(Path.Combine(env.ContentRootPath, ".cache", "alert-state.json")) ?? new();
    private readonly object _lock = new();

    private static string Key(string symbol, string timeframe) => $"{symbol}|{timeframe}";

    // Callers fire this without awaiting it (a Telegram outage must never
    // slow down or fail the dashboard's own data request), so every path
    // through here is swallowed-and-logged rather than left to become an
    // unobserved task exception.
    public async Task CheckAndNotifyAsync(IEnumerable<OrchestratorResult> results)
    {
        try
        {
            foreach (var result in results)
            {
                var key = Key(result.InstrumentSymbol, result.Timeframe);
                var qualifies = result.Success && result.Signal is not null && result.Signal.Grade is "A+" or "A" or "B";

                if (!qualifies)
                {
                    // Clear any prior record so a FUTURE requalification (after
                    // genuinely dropping out) is treated as new, not suppressed
                    // as a stale duplicate of something that's no longer true.
                    bool removed;
                    lock (_lock) { removed = _lastAlerted.Remove(key); }
                    if (removed) Persist();
                    continue;
                }

                var signal = result.Signal!;
                var current = new AlertedState(signal.Direction.ToString(), signal.SetupModel.ToString(), signal.Grade);

                bool isDuplicate;
                lock (_lock)
                {
                    isDuplicate = _lastAlerted.TryGetValue(key, out var previous) && previous == current;
                    if (!isDuplicate) _lastAlerted[key] = current;
                }
                if (isDuplicate) continue;

                var (success, error) = await telegram.SendAsync(TelegramSignalFormatter.Format(signal, qualified: true));
                if (success) Persist();
                else logger.LogWarning("Qualified-signal Telegram alert failed for {Symbol} {Timeframe}: {Error}", result.InstrumentSymbol, result.Timeframe, error);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Signal alert check failed unexpectedly");
        }
    }

    private void Persist()
    {
        Dictionary<string, AlertedState> snapshot;
        lock (_lock) { snapshot = new Dictionary<string, AlertedState>(_lastAlerted); }
        DiskCache.Save(_statePath, snapshot);
    }
}
