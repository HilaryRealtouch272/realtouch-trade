using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;

namespace RealtouchSmartTrade.Api.Services;

// Real and beta share delivery rules, with their own ledger, root and service instance.
public class SignalAlertService(
    TelegramNotifier telegram, SignalLogService signalLog, IHostEnvironment env, ILogger<SignalAlertService> logger,
    TelegramDeliveryOutbox? delivery = null, bool beta = false)
{
    private record AlertedState(string Direction, string SetupModel, string Grade, string? NotificationId = null, bool Accepted = true);

    private readonly string _statePath = Path.Combine(env.ContentRootPath, ".cache", "alert-state.json");
    private readonly Dictionary<string, AlertedState> _lastAlerted =
        DiskCache.Load<Dictionary<string, AlertedState>>(Path.Combine(env.ContentRootPath, ".cache", "alert-state.json")) ?? new();
    private readonly string _statusPath = Path.Combine(env.ContentRootPath, ".cache", "signal-status.json");
    private readonly Dictionary<string, SignalFlowStatus> _statuses =
        DiskCache.Load<Dictionary<string, SignalFlowStatus>>(Path.Combine(env.ContentRootPath, ".cache", "signal-status.json")) ?? new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string Key(string symbol, string timeframe) => $"{symbol}|{timeframe}";

    public IReadOnlyList<SignalFlowStatus> GetStatuses()
    {
        lock (_lock) return _statuses.Values.OrderBy(s => s.Symbol).ThenBy(s => s.Timeframe)
            .Select(s => s with { DeliveryState = delivery?.Get(s.NotificationId)?.Status ?? s.DeliveryState }).ToList();
    }

    private void Record(OrchestratorResult result, string state, string reason, string? notificationId = null)
    {
        lock (_lock) _statuses[Key(result.InstrumentSymbol, result.Timeframe)] =
            SignalFlowStatus.From(result, state, reason, beta, notificationId) with
            {
                DeliveryState = delivery?.Get(notificationId)?.Status ?? (state == "Delivered" ? "Delivered" : null)
            };
    }

    public async Task CheckAndNotifyAsync(IEnumerable<OrchestratorResult> results)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var result in results)
            {
                var blocked = MarketHours.TelegramBlockReason(result.InstrumentSymbol, result.Timeframe, result.Signal?.Grade, DateTime.UtcNow);
                if (blocked is not null)
                {
                    Record(result, "Blocked", blocked);
                    continue;
                }
                var key = Key(result.InstrumentSymbol, result.Timeframe);
                var qualifies = result.Success && result.Signal is { Grade: "A+" or "A" or "B", Triggered: true };
                if (!qualifies)
                {
                    bool removed;
                    lock (_lock) removed = _lastAlerted.Remove(key);
                    if (removed) Persist();
                    var reason = result.Success && result.Signal is not null
                        ? result.Signal.EntryConfirmationReason ?? $"Waiting for a completed {result.Signal.EntryTimeframe} candle in the entry zone and matching structure confirmation"
                        : result.Reason ?? "No setup met its qualification rules";
                    Record(result, result.Success ? "Waiting" : "Rejected", reason);
                    continue;
                }
                var signal = result.Signal!;
                var openDirection = signalLog.GetOpenDirection(result.InstrumentSymbol, result.Timeframe);
                if (openDirection is not null && openDirection != signal.Direction.ToString())
                {
                    Record(result, "Open trade", "A trade in the opposite direction is already open in this flow");
                    continue;
                }
                if (signalLog.IsInCooldown(result.InstrumentSymbol, result.Timeframe, signal.Direction.ToString()))
                {
                    Record(result, "Cooldown", "This flow recently closed a trade in the same direction");
                    continue;
                }

                AlertedState current;
                string? gradeChange = null;
                bool duplicate;
                lock (_lock)
                {
                    _lastAlerted.TryGetValue(key, out var previous);
                    var same = previous is not null && previous.Direction == signal.Direction.ToString() &&
                        previous.SetupModel == signal.SetupModel.ToString() && previous.Grade == signal.Grade;
                    duplicate = same && previous!.Accepted;
                    if (previous is not null && previous.Direction == signal.Direction.ToString() &&
                        previous.SetupModel == signal.SetupModel.ToString() && previous.Grade != signal.Grade)
                        gradeChange = $"{previous.Grade} → {signal.Grade}";
                    current = duplicate ? previous! : new AlertedState(signal.Direction.ToString(), signal.SetupModel.ToString(),
                        signal.Grade, same ? previous!.NotificationId ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"), false);
                    if (!duplicate) _lastAlerted[key] = current;
                }
                if (duplicate)
                {
                    Record(result, "Already announced", "The same qualification has already been accepted for delivery", current.NotificationId);
                    continue;
                }

                Persist();
                // Each timeframe qualifies independently, so an H1+ alert can read as a fresh
                // idea when it's really the same setup already open on a shorter one - flag it.
                var relatedOpen = result.Timeframe != "15m"
                    ? signalLog.GetOpenEntriesOnShorterTimeframes(result.InstrumentSymbol, signal.SetupModel.ToString(), signal.Direction.ToString(), result.Timeframe)
                        .Select(e => e.Timeframe).Distinct().ToList()
                    : null;
                var (success, error) = await telegram.SendAsync(TelegramSignalFormatter.Format(signal, true, gradeChange, relatedOpen),
                    notificationId: current.NotificationId);
                if (success)
                {
                    lock (_lock) _lastAlerted[key] = current with { Accepted = true };
                    Persist();
                    Record(result, delivery is null ? "Delivered" : "Queued", "Entry alert accepted for delivery", current.NotificationId);
                }
                else
                {
                    Record(result, "Delivery failed", "Entry notification could not be accepted; it will retry", current.NotificationId);
                    logger.LogWarning("Qualified-signal alert failed for {Symbol} {Timeframe}: {Error}", result.InstrumentSymbol, result.Timeframe, error);
                }
            }
        }
        catch (Exception ex) { logger.LogError(ex, "Signal alert check failed unexpectedly"); }
        finally
        {
            lock (_lock) DiskCache.Save(_statusPath, _statuses);
            _gate.Release();
        }
    }

    private void Persist()
    {
        lock (_lock) DiskCache.Save(_statePath, _lastAlerted);
    }
}
