using System.Text.Json.Serialization;
using RealtouchSmartTrade.Api.Models;
using RealtouchSmartTrade.Api.Providers;
using RealtouchSmartTrade.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MarketDataService>();
// SignalResult exposes real Strategy/ enums (MarketCondition, SetupDirection,
// SignalState, etc.) straight to the frontend - serialize them as their names
// ("TrendingBullish") rather than raw ints, so the API stays self-describing
// and the frontend never has to hardcode a numeric mapping.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// New provider-adapter layer (section 3) - additive, not yet wired into the
// live /api/setups endpoint below, which still runs the v1 heuristic engine
// so the working UI keeps functioning while the real strategy engine (the
// structure/condition classifiers in Strategy/) is built out incrementally.
builder.Services.AddSingleton<BybitMarketDataProvider>(); // kept for the v1 /api/setups* endpoints only - see SetupDefinition.cs
builder.Services.AddSingleton<IMarketDataProvider>(sp => sp.GetRequiredService<BybitMarketDataProvider>());
builder.Services.AddSingleton<IMarketDataProvider, TwelveDataMarketDataProvider>();
builder.Services.AddSingleton<IMarketDataProvider, CoinbaseMarketDataProvider>();
builder.Services.AddSingleton<IEconomicCalendarProvider, FinnhubEconomicCalendarProvider>();
builder.Services.AddSingleton<INewsProvider, AlphaVantageNewsProvider>();
builder.Services.AddSingleton<TelegramNotifier>();
builder.Services.AddSingleton<SignalOrchestrator>();
builder.Services.AddSingleton<SignalAlertService>();
builder.Services.AddSingleton<SignalLogService>();
builder.Services.AddSingleton<LatestSignalsStore>();
// Always-on scanning independent of the dashboard being open - see its own
// header comment for why this replaces per-request scanning entirely.
builder.Services.AddHostedService<SignalScanBackgroundService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

// GitHub Actions (or any scheduled/cron runner) entry point: run exactly one
// full scan across every instrument/timeframe, send any qualifying Telegram
// alerts (same SignalAlertService, same dedup rules), write a static
// signals.json snapshot, and exit - no Kestrel, no long-running process.
// This is what makes "deploy as a scheduled GitHub Action, no server to run"
// possible: the frontend (hosted statically, e.g. GitHub Pages) fetches that
// JSON file directly instead of calling a live backend that no longer exists
// between runs. Local `dotnet run` (no args) still starts the normal
// always-on server used during development, unaffected by this.
if (args.Contains("--scan-once"))
{
    await RunScanOnceAsync(app.Services);
    return;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Frontend");
app.UseHttpsRedirection();

async Task<SetupSnapshot> BuildSnapshot(SetupDefinition def, MarketDataService marketData, ILogger logger)
{
    try
    {
        IReadOnlyList<Candle> candles;
        string source;
        if (def.Source == DataSource.Bybit)
        {
            candles = await marketData.FetchBybitCandles(def.FeedSymbol, def.BybitCategory, def.BybitInterval);
            source = "Bybit";
        }
        else
        {
            candles = await marketData.FetchTwelveDataCandles(def.FeedSymbol, def.TwelveDataInterval);
            source = "Twelve Data";
        }

        var signal = SignalEngine.Compute(candles);
        return new SetupSnapshot(
            def.Id, def.Symbol, def.Name, def.Group, def.Icon, def.Timeframe, def.Decimals,
            Live: true, LiveSource: source, Error: null,
            Price: signal.Price, Entry: signal.Entry, Stop: signal.Stop, Target: signal.Target, Rr: signal.Rr,
            Direction: signal.Direction, Condition: signal.Condition, Score: signal.Score,
            Confluences: SignalEngine.BuildConfluences(signal, def.Timeframe),
            Reasoning: SignalEngine.BuildReasoning(signal, source, def.Timeframe)
        );
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Live data failed for {Id}", def.Id);
        return new SetupSnapshot(
            def.Id, def.Symbol, def.Name, def.Group, def.Icon, def.Timeframe, def.Decimals,
            Live: false, LiveSource: null, Error: ex.Message,
            Price: null, Entry: null, Stop: null, Target: null, Rr: null,
            Direction: null, Condition: null, Score: null, Confluences: null, Reasoning: null
        );
    }
}

// Twelve Data's free tier caps each key at roughly 8 requests/minute. Firing
// many requests at once (even spread round-robin across several keys) still
// blows that cap instantly, since concurrency alone doesn't limit the RATE
// over time. Dispatching sequentially with a fixed gap does: with N keys
// round-robined one request at a time, a given key is reused only every
// N dispatches, so gap * N must exceed 60s/8 = 7.5s per key. 2s * 4 keys =
// 8s > 7.5s, a safe margin. Bybit has no such limit, so crypto stays parallel.
async Task<List<SetupSnapshot>> BuildSnapshotsPaced(IEnumerable<SetupDefinition> defs, TimeSpan gap, MarketDataService marketData, ILogger logger)
{
    var results = new List<SetupSnapshot>();
    var first = true;
    foreach (var def in defs)
    {
        if (!first) await Task.Delay(gap);
        first = false;
        results.Add(await BuildSnapshot(def, marketData, logger));
    }
    return results;
}

// Split by provider so the frontend can poll Bybit (keyless, no quota) far
// more often than Twelve Data (800 credits/day per key) without burning
// through FX/Metals/Energy quota on every crypto refresh.
app.MapGet("/api/setups/crypto", async (MarketDataService marketData, ILogger<Program> logger) =>
{
    var defs = SetupCatalog.All.Where(d => d.Source == DataSource.Bybit);
    var snapshots = await Task.WhenAll(defs.Select(def => BuildSnapshot(def, marketData, logger)));
    return Results.Ok(snapshots);
})
.WithName("GetCryptoSetups");

app.MapGet("/api/setups/fx", async (MarketDataService marketData, ILogger<Program> logger) =>
{
    var defs = SetupCatalog.All.Where(d => d.Source == DataSource.TwelveData);
    var snapshots = await BuildSnapshotsPaced(defs, TimeSpan.FromSeconds(2), marketData, logger);
    return Results.Ok(snapshots);
})
.WithName("GetFxSetups");

app.MapGet("/api/setups", async (MarketDataService marketData, ILogger<Program> logger) =>
{
    var crypto = SetupCatalog.All.Where(d => d.Source == DataSource.Bybit);
    var fx = SetupCatalog.All.Where(d => d.Source == DataSource.TwelveData);
    var cryptoTask = Task.WhenAll(crypto.Select(def => BuildSnapshot(def, marketData, logger)));
    var fxTask = BuildSnapshotsPaced(fx, TimeSpan.FromSeconds(2), marketData, logger);
    await Task.WhenAll(cryptoTask, fxTask);
    return Results.Ok(cryptoTask.Result.Concat(fxTask.Result));
})
.WithName("GetSetups");

// The REAL strategy engine (Strategy/*.cs), live - not the v1 heuristic
// above. Each instrument x timeframe is evaluated independently; failures
// (no model precondition met, no valid entry/stop/target, stale data) are
// returned as Success:false with an honest Reason, never silently skipped
// or backfilled with a fake result.
// Optional ?timeframe=15m (etc) restricts the scan to one timeframe, so the
// frontend can poll fast-moving timeframes (15m, 1H) far more often than
// slow ones (Weekly, Daily) instead of one blanket interval for everything.
Timeframe[] ResolveTimeframes(string? timeframeParam)
{
    var parsed = TimeframeIntervals.ParseLabel(timeframeParam);
    return parsed is null ? TimeframeIntervals.All : new[] { parsed.Value };
}

// Both endpoints below now READ ONLY from LatestSignalsStore - they never
// call the orchestrator/providers themselves. SignalScanBackgroundService is
// the sole source of real requests to Bybit/Twelve Data/Finnhub/Alpha
// Vantage, on its own always-on schedule, so opening, closing, refreshing,
// or having many browser tabs on the dashboard no longer changes real
// request volume at all - everyone just reads the same latest scan.
static OrchestratorResult PendingResult(string symbol, string timeframe) =>
    new(false, null, "Not yet scanned by the background service - try again shortly.", symbol, timeframe);

app.MapGet("/api/signals/crypto", (LatestSignalsStore store, string? timeframe) =>
{
    var timeframes = ResolveTimeframes(timeframe);
    var results = SetupCatalog.Instruments.Where(i => i.Source == DataSource.Coinbase)
        .SelectMany(instrument => timeframes.Select(tf => (instrument, tf)))
        .Select(x =>
        {
            var label = TimeframeIntervals.Label(x.tf);
            return store.Get(x.instrument.Symbol, label) ?? PendingResult(x.instrument.Symbol, label);
        });
    return Results.Ok(results);
})
.WithName("GetCryptoSignals");

app.MapGet("/api/signals/fx", (LatestSignalsStore store, string? timeframe) =>
{
    var timeframes = ResolveTimeframes(timeframe);
    var results = SetupCatalog.Instruments.Where(i => i.Source == DataSource.TwelveData)
        .SelectMany(instrument => timeframes.Select(tf => (instrument, tf)))
        .Select(x =>
        {
            var label = TimeframeIntervals.Label(x.tf);
            return store.Get(x.instrument.Symbol, label) ?? PendingResult(x.instrument.Symbol, label);
        });
    return Results.Ok(results);
})
.WithName("GetFxSignals");

// Local-dev read of the qualification ledger (see SignalLogService). The
// static/GitHub-Pages deployment instead fetches a signal-log.json snapshot
// written by --scan-once, gated behind the frontend's own passphrase check -
// this live endpoint has no such gate, so it's for local development only.
app.MapGet("/api/signal-log", (SignalLogService signalLog) => Results.Ok(signalLog.GetAll()))
.WithName("GetSignalLog");

// Permanent, deliberate actions on the ledger - local dev only, same as the
// read above. The frontend gates both behind its own confirmation dialog
// before ever calling these; there is no undo once a row (or everything) is
// gone.
app.MapDelete("/api/signal-log/{id}", (SignalLogService signalLog, string id) =>
    signalLog.DeleteEntry(id) ? Results.Ok(new { deleted = true }) : Results.NotFound())
.WithName("DeleteSignalLogEntry");

app.MapDelete("/api/signal-log", (SignalLogService signalLog) =>
{
    signalLog.Reset();
    return Results.Ok(new { reset = true });
})
.WithName("ResetSignalLog");

// Manual test only - confirms the bot token/chat ID work. No automatic
// signal alerts are wired up yet (see TelegramNotifier.cs for why).
app.MapPost("/api/telegram/test", async (TelegramNotifier telegram) =>
{
    var (success, error) = await telegram.SendAsync(
        "✅ Realtouch Smart Trade backend is connected to this Telegram chat. " +
        "No automated signal alerts are enabled yet — this is a manual connectivity test.");
    return success ? Results.Ok(new { sent = true }) : Results.Problem(error, statusCode: 502);
})
.WithName("TestTelegram");

// Manual, on-demand only - a one-off preview/test, independent of the
// automatic per-scan alerting in SignalAlertService (which is what actually
// notifies on real qualification during normal polling; see Program.cs's
// /api/signals/crypto and /api/signals/fx handlers). Scans every crypto
// instrument (Bybit, keyless/no quota) across all 5 timeframes and sends the
// single best REAL result that clears the grade-B qualification bar. If
// nothing currently qualifies, nothing is sent by default and the response
// says so honestly - this never fabricates a "sample" signal. Pass
// ?includeUnqualified=true to instead send the highest-scoring result
// regardless of grade, clearly labeled as a non-tradeable format preview.
// FX/Metals are intentionally excluded here to avoid spending Twelve Data
// quota on an ad-hoc manual request.
app.MapPost("/api/telegram/send-sample", async (SignalOrchestrator orchestrator, TelegramNotifier telegram, bool includeUnqualified) =>
{
    // Paced, not parallel: Coinbase's public API rate-limits at roughly
    // 3 req/sec/IP (confirmed via a real 429 when this ran via Task.WhenAll).
    var results = new List<OrchestratorResult>();
    var first = true;
    foreach (var (instrument, tf) in SetupCatalog.Instruments.Where(i => i.Source == DataSource.Coinbase)
        .SelectMany(instrument => TimeframeIntervals.All.Select(tf => (instrument, tf))))
    {
        if (!first) await Task.Delay(TimeSpan.FromMilliseconds(500));
        first = false;
        results.Add(await orchestrator.Evaluate(instrument, tf));
    }

    var successful = results.Where(r => r.Success && r.Signal is not null).ToList();
    var qualified = successful.Where(r => r.Signal!.Grade is "A+" or "A" or "B")
        .OrderByDescending(r => r.Signal!.SetupQualityScore).ToList();

    OrchestratorResult best;
    bool isQualified;
    if (qualified.Count > 0)
    {
        best = qualified[0];
        isQualified = true;
    }
    else if (includeUnqualified && successful.Count > 0)
    {
        best = successful.OrderByDescending(r => r.Signal!.SetupQualityScore).First();
        isQualified = false;
    }
    else
    {
        return Results.Ok(new
        {
            sent = false,
            scanned = results.Count,
            reason = "No crypto setup currently clears the grade-B qualification bar on any timeframe - nothing was sent. (FX/Metals were not scanned for this manual request to avoid spending Twelve Data quota. Pass ?includeUnqualified=true to send the best available result anyway, clearly labeled as not tradeable.)"
        });
    }

    var (success, error) = await telegram.SendAsync(TelegramSignalFormatter.Format(best.Signal!, isQualified));
    return success
        ? Results.Ok(new { sent = true, qualified = isQualified, symbol = best.InstrumentSymbol, timeframe = best.Timeframe, grade = best.Signal!.Grade, score = best.Signal.SetupQualityScore })
        : Results.Problem(error, statusCode: 502);
})
.WithName("SendSampleTelegramSignal");

async Task RunScanOnceAsync(IServiceProvider services)
{
    var orchestrator = services.GetRequiredService<SignalOrchestrator>();
    var alerts = services.GetRequiredService<SignalAlertService>();
    var signalLog = services.GetRequiredService<SignalLogService>();
    var env = services.GetRequiredService<IHostEnvironment>();
    var scanLogger = services.GetRequiredService<ILogger<Program>>();

    scanLogger.LogInformation("Starting one-shot scan (--scan-once)...");

    // Coinbase's public API rate-limits at roughly 3 req/sec/IP (confirmed
    // via a real 429 when this ran fully parallel) - paced sequentially
    // instead, same reasoning as the FX pacing below.
    var cryptoResults = new List<OrchestratorResult>();
    var firstCrypto = true;
    foreach (var (instrument, tf) in SetupCatalog.Instruments.Where(i => i.Source == DataSource.Coinbase)
        .SelectMany(instrument => TimeframeIntervals.All.Select(tf => (instrument, tf))))
    {
        if (!firstCrypto) await Task.Delay(TimeSpan.FromMilliseconds(500));
        firstCrypto = false;
        cryptoResults.Add(await orchestrator.Evaluate(instrument, tf));
    }

    // FX/Metals: paced 3s between calls (same reasoning as the old per-request
    // /api/signals/fx endpoint) - but NOT every timeframe every run. This
    // workflow's cron fires every 15 minutes; scanning all 5 timeframes on
    // every tick is 4 instruments x 5 timeframes x 96 runs/day = 1,920 Twelve
    // Data calls/day against an 800/day/key free-plan quota - exhausted in
    // hours. Gate each timeframe by TimeframeIntervals.FxPollInterval (the
    // same cadence the always-on SignalScanBackgroundService uses), tracked
    // via a persisted last-scanned timestamp so a one-shot process (no
    // in-memory state between runs) still respects it.
    var schedulePath = Path.Combine(env.ContentRootPath, ".cache", "fx-scan-schedule.json");
    var schedule = DiskCache.Load<Dictionary<string, DateTime>>(schedulePath) ?? new();
    var now = DateTime.UtcNow;

    // A gated timeframe must still show SOMETHING while it waits its turn -
    // carry forward the previous run's own results for it instead of
    // dropping them, and read them from the last written signals.json since
    // that's the only record a stateless one-shot process has of what it
    // last found. Also: "gated" only makes sense once a timeframe has real
    // prior data to fall back on - the very first time (or after a gap with
    // nothing carried forward), run it regardless of the cadence so a
    // setup never just sits empty waiting for a Daily/Weekly turn that's
    // hours away.
    var priorSnapshotPath = Environment.GetEnvironmentVariable("SIGNALS_OUTPUT_PATH")
        ?? Path.Combine(env.ContentRootPath, "signals.json");
    var priorResults = new Dictionary<(string Symbol, string Timeframe), OrchestratorResult>();
    try
    {
        if (File.Exists(priorSnapshotPath))
        {
            var priorDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(priorSnapshotPath));
            var priorJson = System.Text.Json.JsonSerializer.Serialize(priorDoc.RootElement.GetProperty("results"));
            var priorList = System.Text.Json.JsonSerializer.Deserialize<List<OrchestratorResult>>(priorJson,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    Converters = { new JsonStringEnumConverter() }
                }) ?? new();
            foreach (var r in priorList) priorResults[(r.InstrumentSymbol, r.Timeframe)] = r;
        }
    }
    catch (Exception ex)
    {
        scanLogger.LogWarning(ex, "Could not read prior signals.json to carry forward gated timeframes - starting cold");
    }

    var fxResults = new List<OrchestratorResult>();
    var fxInstruments = SetupCatalog.Instruments.Where(i => i.Source == DataSource.TwelveData).ToList();
    foreach (var tf in TimeframeIntervals.All)
    {
        var label = TimeframeIntervals.Label(tf);
        var hasFullPriorCoverage = fxInstruments.All(i => priorResults.ContainsKey((i.Symbol, label)));
        var due = !schedule.TryGetValue(label, out var lastScan) || now - lastScan >= TimeframeIntervals.FxPollInterval(tf);

        if (!due && hasFullPriorCoverage)
        {
            scanLogger.LogInformation("Skipping FX {Timeframe} - last scanned {Ago} ago, carrying forward prior results",
                label, now - lastScan);
            fxResults.AddRange(fxInstruments.Select(i => priorResults[(i.Symbol, label)]));
            continue;
        }

        var first = true;
        foreach (var instrument in fxInstruments)
        {
            if (!first) await Task.Delay(TimeSpan.FromSeconds(3));
            first = false;
            fxResults.Add(await orchestrator.Evaluate(instrument, tf));
        }
        schedule[label] = now;
    }
    DiskCache.Save(schedulePath, schedule);

    var allResults = cryptoResults.Concat(fxResults).ToList();
    await alerts.CheckAndNotifyAsync(allResults);
    await signalLog.RecordAndTrackAsync(allResults);

    // Must match the live API's casing exactly (camelCase) - the frontend's
    // field mapping (applySignalResult in app.component.ts) is written
    // against that shape and doesn't know or care whether the JSON came from
    // a live request or this static snapshot.
    var jsonOptions = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    var outputPath = Environment.GetEnvironmentVariable("SIGNALS_OUTPUT_PATH")
        ?? Path.Combine(env.ContentRootPath, "signals.json");
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) is { Length: > 0 } dir ? dir : ".");
    await File.WriteAllTextAsync(outputPath,
        System.Text.Json.JsonSerializer.Serialize(new { generatedAtUtc = DateTime.UtcNow, results = allResults }, jsonOptions));

    // A separate file, not linked anywhere in the built site's markup/nav -
    // the frontend only fetches it after its own passphrase gate (see
    // app.component.ts). Deliberately named unlike "signals" to avoid it
    // showing up in a casual glance at network requests or file listings.
    var logOutputPath = Environment.GetEnvironmentVariable("SIGNAL_LOG_OUTPUT_PATH")
        ?? Path.Combine(env.ContentRootPath, "signal-log.json");
    Directory.CreateDirectory(Path.GetDirectoryName(logOutputPath) is { Length: > 0 } logDir ? logDir : ".");
    await File.WriteAllTextAsync(logOutputPath,
        System.Text.Json.JsonSerializer.Serialize(signalLog.GetAll(), jsonOptions));

    scanLogger.LogInformation("Scan complete: {Count} results written to {Path}", allResults.Count, outputPath);
}

app.Run();
