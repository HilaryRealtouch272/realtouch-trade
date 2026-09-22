using System.Text.Json;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Services;
using Xunit;

namespace RealtouchSmartTrade.Tests;

public class OrchestratorResultSerializationTests
{
    [Fact]
    public void CandlesNeverReachesSerializedJson()
    {
        // Candles is consumed entirely in-process (SignalLogService's
        // candle-sequence stop/target check) and must never land in
        // signals.json - that file is both the public GitHub Pages payload
        // and what --scan-once reads back as "prior results" for gated
        // timeframes, and dozens of full OHLC candles per instrument/
        // timeframe on every ~15-minute run would bloat both for no reason.
        var candle = new NormalizedCandle("EUR/USD", "EUR/USD", "Test", Timeframe.H1,
            DateTime.UtcNow, DateTime.UtcNow.AddHours(1), 1m, 1.01m, 0.99m, 1m, null, true, DateTime.UtcNow, DataQuality.Ok);
        var result = new OrchestratorResult(false, null, "test", "EUR/USD", "1H", LivePrice: 1m, Candles: new[] { candle });

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("candle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Candles", json);
        // LivePrice must still be there - only Candles is excluded.
        Assert.Contains("LivePrice", json);
    }
}
