using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Strategy;

// Applies entry confirmation to a copy of a qualified setup. The real signal is immutable.
public static class BetaEntryConfirmation
{
    public static SignalResult Confirm(
        SignalResult signal, DateTime setupConfirmedAtUtc, IReadOnlyList<NormalizedCandle> candles, DateTime nowUtc)
    {
        var pending = signal with
        {
            SignalId = $"beta-15m:{signal.SignalId}",
            StrategyVersion = $"beta-15m/{signal.StrategyVersion}",
            EntryTimeframe = "15m",
            Triggered = false,
            Status = SignalState.Watchlist,
            DetectedAtUtc = nowUtc
        };
        var completed = candles
            .Where(c => c.Timeframe == Timeframe.M15 && c.IsComplete && c.Quality == DataQuality.Ok && c.CloseTimeUtc <= nowUtc)
            .OrderBy(c => c.OpenTimeUtc).ToList();
        if (completed.Count < 60 || nowUtc >= signal.ExpiryUtc) return pending;

        var latest = completed[^1];
        pending = pending with { LivePrice = latest.Close };
        if (latest.CloseTimeUtc < setupConfirmedAtUtc ||
            nowUtc - latest.CloseTimeUtc > TimeframeConfig.Duration(Timeframe.M15) * 2.5)
            return pending;

        var structure = StructureAnalyzer.Analyze(completed, Timeframe.M15);
        var isLong = signal.Direction == SetupDirection.Long;
        var trigger = structure.Events.LastOrDefault(e => e.CandleTimeUtc >= setupConfirmedAtUtc &&
            ((isLong && e.Type is StructureEventType.BullishBos or StructureEventType.BullishChoch) ||
             (!isLong && e.Type is StructureEventType.BearishBos or StructureEventType.BearishChoch)));
        var triggered = trigger is not null && latest.Close >= signal.EntryZoneMin && latest.Close <= signal.EntryZoneMax;
        return pending with { Triggered = triggered, Status = triggered ? SignalState.Triggered : SignalState.Watchlist };
    }
}
