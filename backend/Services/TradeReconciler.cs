using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Services;

public record ReconciliationFinding(
    string EntryId, string Symbol, string Timeframe, string Direction, DateTime QualifiedAtUtc,
    string RecordedStatus, decimal? RecordedNetR, string ReplayStatus, decimal? ReplayNetR, string Discrepancy);

public record ReconciliationReport(DateTime GeneratedAtUtc, int Checked, int Skipped, IReadOnlyList<ReconciliationFinding> Findings);

// Replays every open and recently closed paper trade from scratch against the
// provider's one-minute candles and compares the outcome with what the live
// tracker recorded. Any difference - a missed stop, a different order of events,
// a different net R - is reported, so the tracker can never silently drift from
// the market. Markets with no fine feed (FX) are skipped and counted as such,
// never assumed correct.
public class TradeReconciler(IFineCandleSource fineSource, ILogger<TradeReconciler> logger)
{
    private const decimal NetRTolerance = 0.02m;

    public async Task<ReconciliationReport> ReconcileAsync(IReadOnlyList<QualificationLogEntry> entries, DateTime nowUtc, TimeSpan closedWithin)
    {
        var findings = new List<ReconciliationFinding>();
        int checkedCount = 0, skipped = 0;

        foreach (var e in entries.Where(x => TradeSimulator.IsOpen(x.Status) || (x.ClosedAtUtc is { } c && nowUtc - c <= closedWithin)))
        {
            IReadOnlyList<NormalizedCandle> minutes;
            try { minutes = await fineSource.GetOneMinuteAsync(e.Symbol, e.QualifiedAtUtc); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconciliation could not fetch candles for {Symbol}", e.Symbol);
                skipped++; continue;
            }
            if (minutes.Count == 0) { skipped++; continue; }

            checkedCount++;
            // Compare like with like: replay only up to the moment the tracker last looked
            // (its close time, or its watermark while still open).
            var until = e.ClosedAtUtc ?? e.LastProcessedUtc ?? nowUtc;
            var replay = TradeSimulator.ApplyFineSequence(TradeSimulator.ResetForReplay(e), minutes, until.AddMinutes(1));

            var discrepancy = Compare(e, replay);
            if (discrepancy is not null)
                findings.Add(new ReconciliationFinding(e.Id, e.Symbol, e.Timeframe, e.Direction, e.QualifiedAtUtc,
                    e.Status, e.NetRealizedR, replay.Status, replay.NetRealizedR, discrepancy));
        }

        return new ReconciliationReport(nowUtc, checkedCount, skipped, findings);
    }

    internal static string? Compare(QualificationLogEntry recorded, QualificationLogEntry replay)
    {
        // A recorded trade that is still open when the replay says it should have closed is
        // the dangerous case: an adverse move the tracker never saw.
        if (TradeSimulator.IsOpen(recorded.Status) && !TradeSimulator.IsOpen(replay.Status))
            return $"Tracker still shows {recorded.Status} but a one-minute replay closed it as {replay.Status}";
        if (recorded.Status != replay.Status)
            return $"Status differs: recorded {recorded.Status}, replay {replay.Status}";
        if (recorded.NetRealizedR is { } a && replay.NetRealizedR is { } b && Math.Abs(a - b) > NetRTolerance)
            return $"Net R differs: recorded {a:0.00}R, replay {b:0.00}R";
        return null;
    }
}
