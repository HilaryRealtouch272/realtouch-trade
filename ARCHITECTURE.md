# Architecture

## Layout

```
backend/                  ASP.NET Core minimal API (.NET 10)
  Models/                 Data shapes: candles, timeframes, instrument catalog/metadata,
                           SignalResult, MarketHours
  Providers/              External data adapters - Bybit (keyless, registered but no
                           instrument currently routes to it), Twelve Data (FX/metals),
                           Coinbase (crypto), FairEconomy (calendar), Marketaux/Alpha
                           Vantage/GDELT (news), Telegram
  Services/               SignalOrchestrator (the real, live engine), SignalLogService
                           (the paper ledger + Telegram outcome alerts), SignalAlertService
                           (Telegram signal alerts), StrategyDiagnosticsStore,
                           ShadowCandidateStore, TradeReconciler, TradeSimulator,
                           SignalScanBackgroundService (always-on dev server's poll loop),
                           DiskCache (every persisted .cache/*.json file),
                           MarketDataService/SignalEngine (legacy v1 heuristic - see below)
  Strategy/               The real structure/strategy engine - see STRATEGY.md
  Backtesting/            Replays SignalOrchestrator over real historical candles
                           (Backtester.cs), with a no-look-ahead candle provider
                           (ReplayMarketDataProvider.cs), Coinbase history fetch
                           (CoinbaseHistory.cs) and reporting (BacktestStats/Formatter.cs)
  Program.cs              Minimal API endpoints, the `--scan-once`/`--backtest`/
                           `--backtest-shadow` CLI entry points, and the live dev server

backend.Tests/            xUnit tests, deterministic fixtures and real-construction
                           integration tests (no live network calls)

frontend/                 Angular 18 app (standalone components)
  src/app/app.component.ts   Plain imperative DOM rendering (not Angular templates) -
                              the dashboard, signal tracker, diagnostics tab and
                              interactive charts all live in this one file.
```

## The real engine is what's live - this used to not be true, it is now

Earlier in this project there were two separate signal-computation paths: a v1
heuristic actually wired to the live endpoints, and the real structure/strategy
engine sitting untested-in-production alongside it. **That split is gone.** The real
engine (`Services/SignalOrchestrator.cs`, everything under `Strategy/`) is what
every scheduled scan, every Telegram alert, every ledger row and the published
dashboard actually run on.

`Services/MarketDataService.cs`, `Services/SignalEngine.cs` and the `/api/setups*`
endpoints in `Program.cs` are the old v1 heuristic (EMA20/EMA50 + ATR, fixed 2R).
They still exist and still compile, but the frontend does not call them - they are
unused legacy code, kept only because removing them hasn't been a priority. They
should eventually be deleted rather than mistaken for part of the live path.

## How a scan actually happens

```
GitHub Actions cron (every 15 minutes, scan-and-deploy.yml)
  --> dotnet ... --scan-once
      --> warm-starts backend/.cache/*.json from the `bot-state` branch
          (calendar, news, context/HTF, alert-dedup, outcome-alert-dedup,
           FX scan schedule, exhausted-key tracking, diagnostics, shadow candidates)
      --> SignalOrchestrator.Evaluate(instrument, timeframe) for every
          (instrument x timeframe) pair - crypto every run, FX only on the
          timeframes that are due
      --> SignalAlertService.CheckAndNotifyAsync   (Telegram: new/changed signals)
      --> SignalLogService.RecordAndTrackAsync     (the paper ledger; Telegram: outcomes)
      --> StrategyDiagnosticsStore / ShadowCandidateStore record every model's
          evaluation and the shadow-mode candidates, qualified or not
      --> writes signals.json, signal-log.json, diagnostics.json,
          reconciliation.json, shadow-candidates.json
      --> persists backend/.cache/*.json back to the `bot-state` branch
      --> builds the Angular frontend, copies those JSON files alongside it,
          publishes to GitHub Pages
```

The published site has no backend behind it - it fetches those static JSON files
directly. Local `dotnet run` (no args) instead starts an always-on dev server
(`SignalScanBackgroundService`) that polls on its own schedule and serves
`/api/signals/*` live, for development only.

## Inside one evaluation (`Strategy/*.cs`, orchestrated by `SignalOrchestrator`)

```
NormalizedCandle[] (per timeframe, from the provider)
  --> CandleQualityChecker (stale/gap/duplicate/out-of-order)
  --> PivotDetector --> StructureAnalyzer (BOS/CHoCH) --> MarketConditionClassifier
  --> FvgDetector, OrderBlockDetector, PremiumDiscount, WillisZoneDetector,
      LiquiditySweepDetector, KeyLevelCatalog (incl. higher-timeframe levels)
  --> SetupModels.Evaluate* (4 independent models, each its own mandatory gates)
  --> ModelRouting (Market Condition gate) + common gates (confluence count, cost ratio)
  --> EntryStopTargetCalculator / RangeTradePlan --> ModelScoring (per-model 100pt score)
  --> RiskSizing --> SignalResultBuilder --> SignalResult
  --> (separately) ShadowStrategies.Evaluate - 4 more models, recorded only, never alerted
```

Every evaluation (qualified or not) is recorded by `StrategyDiagnosticsStore`; the
highest-scoring qualified one becomes the primary signal for that scan.

## Fill simulation and the paper ledger

A triggered, grade-B-or-better signal becomes a `QualificationLogEntry`
(`SignalLogService`). `TradeSimulator` is the single source of truth for what
happens to it afterwards - stop/target fills, partial exits, costs, net R,
MFE/MAE - and is reused unchanged by both the live ledger and the backtester, so
there is no second implementation of the fill rules to drift. Crypto trades settle
from 1-minute candles with a persisted watermark so a restart resumes exactly
where it left off; `TradeReconciler` independently replays open and recently
closed crypto trades from scratch to catch any drift.

## Persistence

There is no database. Everything that needs to survive between scans (the
calendar, news and HTF-context caches, the Telegram alert/outcome dedup state, the
FX scan schedule, exhausted-API-key tracking, the paper ledger itself, the
strategy-diagnostics history and daily rollup, and the shadow-mode candidates) is a
JSON file under `backend/.cache/`, persisted to and restored from the `bot-state`
git branch by the workflow. `Services/DiskCache.cs` is the shared load/save helper.

## Secrets

All third-party API keys live in `dotnet user-secrets` locally and as GitHub
Actions secrets in CI (see `ENVIRONMENT.md`) - never in the repository or a
committed file. `.env` exists locally as a scratch reference only and is
gitignored; `.env.example` lists variable names with no values.

## Known gaps (see STRATEGY.md for the full, current list)

- The legacy `/api/setups*` v1 heuristic path is still present and unused - it
  should be deleted rather than left to confuse the next reader.
- Forex and metals have no 1-minute settlement or reconciliation (no free deep
  intraday history source); they settle on their own timeframe's candles.
- No historical calendar/news feed exists, so the backtester cannot simulate the
  calendar veto or news state - only live scans see those.
- `VALIDATION.md` predates almost everything described above and needs a rewrite.
