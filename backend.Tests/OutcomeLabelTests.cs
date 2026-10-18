using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

// Regression: a stopped-out trade that banked TP1/TP2 before the remainder stopped was being
// labelled "Stopped out — LOSS" purely from its Status, even when its net R was positive.
// Stop-triggered and economically profitable are different concepts; the label must follow
// the net result, not just which event closed the trade.
public class OutcomeLabelTests
{
    private static QualificationLogEntry Entry(string status, DateTime? tp1 = null, DateTime? tp2 = null) => new(
        Id: "t", Symbol: "GBP/USD", Timeframe: "Daily", Direction: "Short", SetupModel: "TrendContinuationPullback",
        Grade: "B", Score: 83, Entry: 1.3270m, Stop: 1.3298m, Tp1: 1.3242m, Tp2: 1.3205m, Tp3: 1.2101m, RewardToRisk: 2.3m,
        QualifiedAtUtc: DateTime.UtcNow, TrackingExpiryUtc: DateTime.UtcNow.AddDays(1),
        Status: status, Tp1HitAtUtc: tp1, Tp2HitAtUtc: tp2, ClosedAtUtc: null, RealizedR: null);

    [Fact]
    public void AStopOutThatBankedProfitFirstAndFinishesNetPositiveIsNeverCalledALoss()
    {
        // The real GBP/USD case: TP2 was hit, then the remainder stopped, net +1.12R.
        var entry = Entry("StoppedOut", tp1: DateTime.UtcNow, tp2: DateTime.UtcNow);

        var (icon, label) = SignalLogService.OutcomeLabel(entry, realizedR: 1.12m);

        Assert.DoesNotContain("LOSS", label);
        Assert.Contains("net profit", label);
        Assert.Equal("🟢", icon);
    }

    [Fact]
    public void AStopOutThatFinishesNetNegativeIsStillCalledALoss()
    {
        var entry = Entry("StoppedOut");

        var (icon, label) = SignalLogService.OutcomeLabel(entry, realizedR: -1.05m);

        Assert.Contains("LOSS", label);
        Assert.Equal("🛑", icon);
    }

    [Fact]
    public void AStopOutNearZeroNetIsCalledBreakevenNotAWinOrALoss()
    {
        var entry = Entry("StoppedOut");

        var (_, label) = SignalLogService.OutcomeLabel(entry, realizedR: 0.001m);

        Assert.Contains("breakeven", label);
        Assert.DoesNotContain("LOSS", label);
        Assert.DoesNotContain("profit", label);
    }

    [Fact]
    public void ATp3HitThatSomehowNetsNegativeAfterCostsIsNotCalledAWin()
    {
        var entry = Entry("Tp3Hit");

        var (icon, label) = SignalLogService.OutcomeLabel(entry, realizedR: -0.1m);

        Assert.DoesNotContain("WIN", label);
        Assert.Contains("net loss", label);
        Assert.Equal("🛑", icon);
    }

    [Fact]
    public void ATp3HitThatIsGenuinelyPositiveIsStillCalledAWin()
    {
        var entry = Entry("Tp3Hit");

        var (icon, label) = SignalLogService.OutcomeLabel(entry, realizedR: 3.2m);

        Assert.Contains("WIN", label);
        Assert.Equal("🏆", icon);
    }

    [Fact]
    public void ProgressEventsTp1AndTp2CarryNoWinLossVerdict()
    {
        Assert.Equal("TP1 hit", SignalLogService.OutcomeLabel(Entry("Tp1Hit"), 1m).Label);
        Assert.Equal("TP2 hit", SignalLogService.OutcomeLabel(Entry("Tp2Hit"), 1m).Label);
    }
}
