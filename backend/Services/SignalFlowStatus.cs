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

public static class SignalComparison
{
    public static object Build(IEnumerable<QualificationLogEntry> real, IEnumerable<QualificationLogEntry> beta)
    {
        var realRows = real.Where(e => e.SourceSetupId is not null).ToList();
        var betaRows = beta.Where(e => e.SourceSetupId is not null).ToList();
        return realRows.Concat(betaRows).GroupBy(e => e.SourceSetupId)
            .OrderByDescending(g => g.Max(e => e.QualifiedAtUtc)).Take(50).Select(g =>
            {
                var entry = g.First();
                return new
                {
                    sourceSetupId = g.Key, entry.Symbol, entry.Timeframe, entry.SetupModel,
                    real = Summarize(realRows.Where(e => e.SourceSetupId == g.Key).ToList()),
                    beta = Summarize(betaRows.Where(e => e.SourceSetupId == g.Key).ToList())
                };
            }).ToList();
    }

    private static object Summarize(List<QualificationLogEntry> rows)
    {
        var closed = rows.Where(e => !TradeSimulator.IsOpen(e.Status)).ToList();
        return new
        {
            trades = rows.Count, resolved = closed.Count,
            firstEntryUtc = rows.OrderBy(e => e.QualifiedAtUtc).FirstOrDefault()?.QualifiedAtUtc,
            netR = closed.Count == 0 || closed.Any(e => e.NetRealizedR is null)
                ? (decimal?)null : closed.Sum(e => e.NetRealizedR!.Value)
        };
    }
}

public static class SignalStatusReport
{
    public static object Build(SignalAlertService alerts, SignalLogService ledger,
        TelegramDeliveryOutbox delivery, BetaConfirmationFlow beta) => new
    {
        generatedAtUtc = DateTime.UtcNow,
        real = alerts.GetStatuses(), beta = beta.GetStatuses(),
        realDelivery = delivery.Summarize(), betaDelivery = beta.GetDeliverySummary(),
        comparison = SignalComparison.Build(ledger.GetAll(), beta.GetAll())
    };
}
