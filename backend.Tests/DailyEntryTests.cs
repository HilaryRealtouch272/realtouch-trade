using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Strategy;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class DailyEntryTests
{
    private static readonly DateTime Start = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static List<NormalizedCandle> EntryCandles(SetupDirection direction = SetupDirection.Long) =>
        CandleFixtures.FromOhlc(
            direction == SetupDirection.Long ? CandleFixtures.UptrendBars(96) : CandleFixtures.DowntrendBars(96),
            Timeframe.M15, Start);

    private static EntryPlan DailyEntry(decimal price, DateTime now, bool triggered = false) =>
        new(price - 0.5m, price + 0.5m, price, price, null, Timeframe.Daily,
            now.AddDays(2), price - 5m, triggered, "OrderBlock");

    [Theory]
    [InlineData(SetupDirection.Long)]
    [InlineData(SetupDirection.Short)]
    public void FreshFifteenMinuteConfirmationCanTriggerADailyEntry(SetupDirection direction)
    {
        var candles = EntryCandles(direction);
        var latest = candles[^1];
        var now = latest.CloseTimeUtc.AddMinutes(1);
        var entry = DailyEntry(latest.Close, now);

        var confirmed = EntryStopTargetCalculator.ConfirmEntry(entry, direction, candles,
            TimeframeHierarchy.EntryTimeframe(Timeframe.Daily), now.AddHours(-12), now);

        Assert.True(confirmed.Triggered);
        Assert.Equal(Timeframe.M15, confirmed.EntryTimeframe);
        Assert.True(confirmed.TriggerCandleTimeUtc >= now.AddHours(-12));
        Assert.Equal(entry.ZoneMin, confirmed.ZoneMin);
        Assert.Equal(entry.ZoneMax, confirmed.ZoneMax);
        Assert.Equal(entry.PreferredEntry, confirmed.PreferredEntry);
        Assert.Equal(entry.ExpiryUtc, confirmed.ExpiryUtc);
    }

    [Fact]
    public void AnEarlierDailyTriggerCannotBypassMissingOrStaleEntryData()
    {
        var candles = EntryCandles();
        var now = candles[^1].CloseTimeUtc.AddHours(2);
        var entry = DailyEntry(candles[^1].Close, now, triggered: true);

        foreach (var data in new IReadOnlyList<NormalizedCandle>[] { candles, Array.Empty<NormalizedCandle>() })
        {
            var confirmed = EntryStopTargetCalculator.ConfirmEntry(entry, SetupDirection.Long, data,
                Timeframe.M15, now.AddHours(-12), now);
            Assert.False(confirmed.Triggered);
            Assert.Null(confirmed.TriggerCandleTimeUtc);
            Assert.Equal(Timeframe.M15, confirmed.EntryTimeframe);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFormingOrFutureCandleCannotSupplyTheEntry(bool complete)
    {
        var candles = EntryCandles();
        var latest = candles[^1];
        var now = latest.CloseTimeUtc.AddMinutes(1);
        var price = latest.Close + 20m;
        candles.Add(latest with
        {
            OpenTimeUtc = latest.CloseTimeUtc,
            CloseTimeUtc = latest.CloseTimeUtc.AddMinutes(15),
            High = price + 1m, Low = price - 1m, Close = price, IsComplete = complete
        });

        var confirmed = EntryStopTargetCalculator.ConfirmEntry(DailyEntry(price, now), SetupDirection.Long,
            candles, Timeframe.M15, now.AddHours(-12), now);

        Assert.False(confirmed.Triggered);
    }

    [Fact]
    public void EntryConfirmationMustFollowTheCompletedDailySetupAndRespectItsExpiry()
    {
        var candles = EntryCandles();
        var now = candles[^1].CloseTimeUtc.AddMinutes(1);
        var entry = DailyEntry(candles[^1].Close, now);

        var beforeSetup = EntryStopTargetCalculator.ConfirmEntry(entry, SetupDirection.Long, candles,
            Timeframe.M15, now, now);
        var expired = EntryStopTargetCalculator.ConfirmEntry(entry with { ExpiryUtc = now },
            SetupDirection.Long, candles, Timeframe.M15, now.AddHours(-12), now);

        Assert.False(beforeSetup.Triggered);
        Assert.False(expired.Triggered);
    }

    [Fact]
    public void PriceInsideTheZoneStillNeedsAMatchingStructureEvent()
    {
        var candles = CandleFixtures.FromOhlc(CandleFixtures.RangingBars(96), Timeframe.M15, Start);
        var now = candles[^1].CloseTimeUtc.AddMinutes(1);
        var entry = DailyEntry(candles[^1].Close, now);

        var confirmed = EntryStopTargetCalculator.ConfirmEntry(entry, SetupDirection.Long,
            candles, Timeframe.M15, now.AddHours(-12), now);

        Assert.False(confirmed.Triggered);
    }

    [Fact]
    public void DailyEntryChecksRunAsOftenAsTheFifteenMinuteFeed()
    {
        Assert.Equal(TimeframeIntervals.FxPollInterval(Timeframe.M15), TimeframeIntervals.FxPollInterval(Timeframe.Daily));
    }
}
