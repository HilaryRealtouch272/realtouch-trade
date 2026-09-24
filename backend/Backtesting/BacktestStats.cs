using RealtouchSmartTrade.Api.Services;
using RealtouchSmartTrade.Api.Strategy;

namespace RealtouchSmartTrade.Api.Backtesting;

public record SegmentStats(
    string Name, DateTime? From, DateTime? To, int Trades, int NetProfitable, double NetProfitableRate,
    double WilsonLower, double WilsonUpper, int Tp3, int StopTriggered, double ExpectancyR, double TotalR,
    double ProfitFactor, double MaxDrawdownR, int UncertainSequences, bool Provisional);

// Pure statistics over closed simulated trades. Rates are net-profitable (net R
// after costs above breakeven), never a status label; every rate carries its
// Wilson interval and a provisional flag below 30 trades.
public static class BacktestStats
{
    private const double EpsR = 0.005;

    public static double NetR(QualificationLogEntry e) => (double)(e.NetRealizedR ?? e.RealizedR ?? 0m);

    public static SegmentStats Segment(string name, IReadOnlyList<QualificationLogEntry> trades)
    {
        var ordered = trades.OrderBy(t => t.ClosedAtUtc ?? t.QualifiedAtUtc).ToList();
        var n = ordered.Count;
        var wins = ordered.Count(t => NetR(t) > EpsR);
        var wilson = WilsonIntervalCalculator.Compute(wins, n);
        var gain = ordered.Where(t => NetR(t) > 0).Sum(NetR);
        var loss = -ordered.Where(t => NetR(t) < 0).Sum(NetR);
        var total = ordered.Sum(NetR);

        // Peak-to-trough of the cumulative R curve in closing order.
        double equity = 0, peak = 0, maxDd = 0;
        foreach (var t in ordered)
        {
            equity += NetR(t);
            peak = Math.Max(peak, equity);
            maxDd = Math.Max(maxDd, peak - equity);
        }

        return new SegmentStats(name,
            ordered.Count > 0 ? ordered.Min(t => t.QualifiedAtUtc) : null,
            ordered.Count > 0 ? ordered.Max(t => t.ClosedAtUtc ?? t.QualifiedAtUtc) : null,
            n, wins, n == 0 ? 0 : (double)wins / n, wilson.Lower, wilson.Upper,
            ordered.Count(t => t.Status == "Tp3Hit"), ordered.Count(t => t.Status == "StoppedOut"),
            n == 0 ? 0 : total / n, total,
            loss == 0 ? (gain > 0 ? double.PositiveInfinity : 0) : gain / loss,
            maxDd, ordered.Count(t => t.IntrabarSequenceUncertain), n < 30);
    }

    // Successive equal-count folds in time order: the honest meaning of walk-forward
    // for a rule set that is not being refit - does it hold up period after period?
    public static IReadOnlyList<SegmentStats> WalkForward(IReadOnlyList<QualificationLogEntry> trades, int folds)
    {
        var ordered = trades.OrderBy(t => t.QualifiedAtUtc).ToList();
        if (ordered.Count == 0 || folds < 1) return Array.Empty<SegmentStats>();
        folds = Math.Min(folds, ordered.Count);
        var result = new List<SegmentStats>();
        for (var i = 0; i < folds; i++)
        {
            var from = ordered.Count * i / folds;
            var to = ordered.Count * (i + 1) / folds;
            result.Add(Segment($"Fold {i + 1}/{folds}", ordered.GetRange(from, to - from)));
        }
        return result;
    }

    // Chronological split: the first `inSampleFraction` of trades in time, then the rest.
    // Nothing is tuned on the in-sample part in this project, so the out-of-sample
    // segment is a genuine check on later data, not a fit-then-test artefact.
    public static (SegmentStats InSample, SegmentStats OutOfSample) InOutOfSample(IReadOnlyList<QualificationLogEntry> trades, double inSampleFraction = 0.7)
    {
        var ordered = trades.OrderBy(t => t.QualifiedAtUtc).ToList();
        var cut = (int)Math.Round(ordered.Count * inSampleFraction);
        return (Segment("In-sample", ordered.GetRange(0, cut)), Segment("Out-of-sample", ordered.GetRange(cut, ordered.Count - cut)));
    }
}
