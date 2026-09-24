using System.Text;

namespace RealtouchSmartTrade.Api.Backtesting;

public static class BacktestFormatter
{
    public static string Format(BacktestReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BACKTEST {r.Symbol} {r.Timeframe}  {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}");
        sb.AppendLine($"Evaluations {r.Evaluations}, qualified signals seen {r.SignalsSeen}, trades closed {r.TradesClosed}, unresolved at end {r.TradesUnresolved}");
        sb.AppendLine();
        Row(sb, r.Overall); Row(sb, r.InSample); Row(sb, r.OutOfSample);
        sb.AppendLine("Walk-forward folds:");
        foreach (var f in r.WalkForward) Row(sb, f);
        sb.AppendLine("By model:");
        foreach (var m in r.ByModel) Row(sb, m);
        sb.AppendLine();
        foreach (var n in r.Notes) sb.AppendLine("Note: " + n);
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, SegmentStats s)
    {
        var pf = double.IsInfinity(s.ProfitFactor) ? "n/a" : s.ProfitFactor.ToString("0.00");
        sb.AppendLine($"  {s.Name,-26} n={s.Trades,3}{(s.Provisional ? " (provisional)" : "")}  net-profitable {s.NetProfitableRate:P0} " +
                      $"[{s.WilsonLower:P0}-{s.WilsonUpper:P0}]  TP3 {s.Tp3}  stopped {s.StopTriggered}  expectancy {s.ExpectancyR:0.00}R  " +
                      $"total {s.TotalR:0.0}R  PF {pf}  maxDD {s.MaxDrawdownR:0.0}R  seq-uncertain {s.UncertainSequences}");
    }
}
