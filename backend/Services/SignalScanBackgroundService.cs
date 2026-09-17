using RealtouchSmartTrade.Api.Models;

namespace RealtouchSmartTrade.Api.Services;

// Always-on server-side scanning, independent of whether the dashboard is
// open. Runs one loop for crypto (Bybit, keyless/no quota - all 5 timeframes
// together) and one loop per FX/Metals timeframe (Twelve Data, quota- and
// rate-limited - each on its own cadence matched to how fast that timeframe's
// candle actually closes, same reasoning the frontend's polling used to
// apply itself before this moved server-side). Every scan writes into
// LatestSignalsStore (what /api/signals/* now serves) and feeds
// SignalAlertService (what sends qualified-signal Telegram alerts) - so this
// is now the ONLY thing that calls the market-data/news/calendar providers;
// the dashboard being open, closed, or refreshed no longer changes real
// request volume against Bybit/Twelve Data/Alpha Vantage/Finnhub at all.
public class SignalScanBackgroundService(
    SignalOrchestrator orchestrator,
    SignalAlertService alerts,
    SignalLogService signalLog,
    LatestSignalsStore store,
    ILogger<SignalScanBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan CryptoInterval = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<Timeframe, TimeSpan> FxIntervals = new()
    {
        [Timeframe.M15] = TimeSpan.FromMinutes(10),
        [Timeframe.H1] = TimeSpan.FromMinutes(20),
        [Timeframe.H4] = TimeSpan.FromMinutes(45),
        [Timeframe.Daily] = TimeSpan.FromHours(2),
        [Timeframe.Weekly] = TimeSpan.FromHours(6)
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task> { RunLoop("Crypto", CryptoInterval, TimeSpan.Zero, ct => ScanCryptoAsync(ct), stoppingToken) };

        // Stagger each FX timeframe's first run so all 20 (4 instruments x 5
        // timeframes) evaluations don't all fire in the same burst at startup.
        var startDelay = TimeSpan.Zero;
        foreach (var tf in TimeframeIntervals.All)
        {
            loops.Add(RunLoop($"FX-{TimeframeIntervals.Label(tf)}", FxIntervals[tf], startDelay, ct => ScanFxAsync(tf, ct), stoppingToken));
            startDelay += TimeSpan.FromSeconds(20);
        }

        await Task.WhenAll(loops);
    }

    private async Task RunLoop(string label, TimeSpan interval, TimeSpan initialDelay, Func<CancellationToken, Task> scan, CancellationToken ct)
    {
        try
        {
            if (initialDelay > TimeSpan.Zero) await Task.Delay(initialDelay, ct);
        }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await scan(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Label} background scan failed", label);
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Coinbase's public (keyless) API rate-limits at roughly 3 req/sec/IP -
    // confirmed via a real 429 when this ran fully parallel (fine for Bybit,
    // which had no such limit). Paced sequentially instead, same pattern as
    // FX below; weekly bars alone can cost several paginated calls per
    // instrument, so this stays well under the limit rather than bursting.
    private async Task ScanCryptoAsync(CancellationToken ct)
    {
        var pairs = SetupCatalog.Instruments.Where(i => i.Source == DataSource.Coinbase)
            .SelectMany(instrument => TimeframeIntervals.All.Select(tf => (instrument, tf)))
            .ToList();

        var results = new List<OrchestratorResult>();
        var first = true;
        foreach (var (instrument, tf) in pairs)
        {
            if (!first) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            first = false;
            results.Add(await orchestrator.Evaluate(instrument, tf));
        }
        foreach (var result in results) store.Set(result);
        await alerts.CheckAndNotifyAsync(results);
        await signalLog.RecordAndTrackAsync(results);
    }

    private async Task ScanFxAsync(Timeframe timeframe, CancellationToken ct)
    {
        var results = new List<OrchestratorResult>();
        var first = true;
        foreach (var instrument in SetupCatalog.Instruments.Where(i => i.Source == DataSource.TwelveData))
        {
            if (!first) await Task.Delay(TimeSpan.FromSeconds(3), ct);
            first = false;
            var result = await orchestrator.Evaluate(instrument, timeframe);
            store.Set(result);
            results.Add(result);
        }
        await alerts.CheckAndNotifyAsync(results);
        await signalLog.RecordAndTrackAsync(results);
    }
}
