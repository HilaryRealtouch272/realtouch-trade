using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Services;

public record SignalFlowStatus(
    string Symbol, string Timeframe, string EntryTimeframe, string EntryState, string AlertState,
    string Reason, string? Model, string? Grade, string? SourceSetupId, DateTime? ConfirmationCandleUtc,
    DateTime ObservedAtUtc, string? NotificationId = null, string? DeliveryState = null)
{
    public static SignalFlowStatus From(OrchestratorResult result, string alertState, string reason,
        bool beta, string? notificationId = null)
    {
        var signal = result.Signal;
        var best = result.AllEvaluations?.OrderByDescending(e => e.Score).FirstOrDefault();
        var gates = best?.FailedGates.Select(g => g.ToString().Replace('_', ' ').ToLowerInvariant()).ToList();
        if (!result.Success && gates is { Count: > 0 }) reason += " · " + string.Join(", ", gates);
        return new(result.InstrumentSymbol, result.Timeframe, signal?.EntryTimeframe ?? (beta ? "15m" : result.Timeframe),
            result.Success && signal is not null ? signal.Triggered ? "Triggered" : "Waiting for entry" : "No qualified setup",
            alertState, reason, signal?.SetupModel.ToString() ?? best?.StrategyId.ToString(), signal?.Grade,
            signal is null ? null : SignalIdentity.For(signal), signal?.ConfirmationCandleUtc,
            DateTime.UtcNow, notificationId);
    }
}

public static class SignalStatusReport
{
    public static object Build(SignalAlertService alerts, TelegramDeliveryOutbox delivery) => new
    {
        generatedAtUtc = DateTime.UtcNow,
        real = alerts.GetStatuses(),
        realDelivery = delivery.Summarize()
    };
}
