using Microsoft.Extensions.FileProviders;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Services;

// Own service instances, configuration and disk root keep beta entries and delivery state
// separate from the real ledger. Inputs are read-only copies of the real scan's results.
public class BetaConfirmationFlow
{
    private record PendingSetup(SignalResult Signal, DateTime ConfirmedAtUtc);
    private record CalendarSnapshot(CalendarResult Result);

    private class BetaEnvironment(IHostEnvironment real) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = real.ApplicationName;
        public string EnvironmentName { get; set; } = real.EnvironmentName;
        public string ContentRootPath { get; set; } = Path.Combine(real.ContentRootPath, ".cache", "beta-15m");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly SignalLogService _ledger;
    private readonly SignalAlertService _alerts;
    private readonly TelegramDeliveryOutbox _outbox;
    private readonly ILogger<BetaConfirmationFlow> _logger;
    private readonly string _setupsPath;
    private readonly string _calendarPath;
    private readonly Dictionary<string, PendingSetup> _setups;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTime> _clock;

    public BetaConfirmationFlow(
        IHttpClientFactory httpClients, IConfiguration config, IHostEnvironment env, ILoggerFactory loggers,
        Func<DateTime>? clock = null, IFineCandleSource? fineSource = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = loggers.CreateLogger<BetaConfirmationFlow>();
        var betaEnv = new BetaEnvironment(env);
        _setupsPath = Path.Combine(betaEnv.ContentRootPath, ".cache", "setups.json");
        _calendarPath = Path.Combine(env.ContentRootPath, ".cache", "calendar-cache.json");
        _setups = DiskCache.Load<Dictionary<string, PendingSetup>>(_setupsPath) ?? new();

        var betaChat = config["Telegram:BetaChatId"]?.Trim();
        if (string.IsNullOrWhiteSpace(betaChat) || betaChat == config["Telegram:ChatId"]?.Trim())
        {
            _logger.LogWarning("Beta Telegram delivery disabled: configure a BetaChatId distinct from the real ChatId");
            betaChat = "";
        }
        var betaConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = config["Telegram:BotToken"],
            ["Telegram:ChatId"] = betaChat
        }).Build();
        _outbox = new TelegramDeliveryOutbox(httpClients, betaConfig, betaEnv, loggers.CreateLogger<TelegramDeliveryOutbox>(), beta: true);
        var telegram = new TelegramNotifier(_outbox, betaConfig);
        _ledger = new SignalLogService(telegram, betaEnv, loggers.CreateLogger<SignalLogService>(), fineSource);
        _alerts = new SignalAlertService(telegram, _ledger, betaEnv, loggers.CreateLogger<SignalAlertService>(), _outbox, beta: true);
    }

    public IReadOnlyList<QualificationLogEntry> GetAll() => _ledger.GetAll();
    public IReadOnlyList<SignalFlowStatus> GetStatuses() => _alerts.GetStatuses();
    public object GetDeliverySummary() => _outbox.Summarize();

    public async Task ProcessAsync(IEnumerable<OrchestratorResult> results)
    {
        await _gate.WaitAsync();
        try
        {
            var batch = results.ToList();
            var rejected = new List<OrchestratorResult>();
            foreach (var result in batch.Where(r => r.Timeframe is "Weekly" or "Daily" or "4H" or "1H"))
            {
                var key = $"{result.InstrumentSymbol}|{result.Timeframe}";
                if (!result.Success || result.Signal is not { Grade: "A+" or "A" or "B" } signal)
                {
                    _setups.Remove(key);
                    rejected.Add(result);
                    continue;
                }
                var completed = result.Candles?
                    .Where(c => c.IsComplete && c.Quality == DataQuality.Ok).OrderBy(c => c.CloseTimeUtc).LastOrDefault();
                if (completed is not null)
                    _setups[key] = new PendingSetup(signal, completed.CloseTimeUtc);
                else if (_setups.TryGetValue(key, out var prior))
                    _setups[key] = prior with { Signal = signal };
            }
            DiskCache.Save(_setupsPath, _setups);

            var fineResults = batch.Where(r => r.Timeframe == "15m" && r.Candles is { Count: > 0 })
                .GroupBy(r => r.InstrumentSymbol).ToDictionary(g => g.Key, g => g.First());
            var confirmed = new List<OrchestratorResult>();
            var waiting = new List<OrchestratorResult>();
            var calendar = DiskCache.Load<CalendarSnapshot>(_calendarPath)?.Result
                ?? new CalendarResult(false, "Calendar unavailable", Array.Empty<EconomicEvent>());
            foreach (var setup in _setups.Values)
            {
                if (!fineResults.TryGetValue(setup.Signal.Symbol, out var fine))
                {
                    var pending = BetaEntryConfirmation.Confirm(setup.Signal, setup.ConfirmedAtUtc,
                        Array.Empty<NormalizedCandle>(), _clock());
                    waiting.Add(new OrchestratorResult(true, pending, null, pending.Symbol, pending.AnalysisTimeframe));
                    continue;
                }
                var signal = BetaEntryConfirmation.Confirm(setup.Signal, setup.ConfirmedAtUtc, fine.Candles!, _clock());
                var instrument = SetupCatalog.Instruments.FirstOrDefault(i => i.Symbol == signal.Symbol);
                var veto = EconomicCalendarVeto.Evaluate(calendar, instrument?.AffectedCurrencies ?? Array.Empty<string>(), _clock());
                if (veto.State == CalendarVetoState.HardVeto)
                {
                    confirmed.Add(new OrchestratorResult(false, null, $"Hard economic-calendar veto active: {veto.Reason}",
                        signal.Symbol, signal.AnalysisTimeframe, LivePrice: signal.LivePrice, Candles: fine.Candles));
                    continue;
                }
                signal = signal with { EconomicCalendarState = $"{veto.State}: {veto.Reason}" };
                confirmed.Add(new OrchestratorResult(true, signal, null, signal.Symbol, signal.AnalysisTimeframe,
                    LivePrice: signal.LivePrice, Candles: fine.Candles));
            }
            // Price-only copies settle existing beta trades without opening native 15m trades.
            // Alert BEFORE recording into the ledger (same order the real flow uses) - otherwise
            // a freshly-confirming entry would already read back as "open" by the time the alert
            // check runs, and SignalAlertService now blocks any alert for an already-open trade.
            var prices = fineResults.Values.Select(r => r with { Success = false, Signal = null, AllEvaluations = null });
            await _alerts.CheckAndNotifyAsync(confirmed.Concat(waiting).Concat(rejected));
            await _ledger.RecordAndTrackAsync(confirmed.Concat(prices));
            await _outbox.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Beta 15m confirmation failed");
        }
        finally { _gate.Release(); }
    }

}
