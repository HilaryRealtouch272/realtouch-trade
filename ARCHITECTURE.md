# Architecture

## Layout

```
backend/                  ASP.NET Core minimal API (.NET 10)
  Models/                 Data shapes: candles, timeframes, setup catalog, SignalResult
  Providers/              External data adapters (Bybit, Twelve Data, Coinbase, the FairEconomy calendar feed, Marketaux/Alpha Vantage/GDELT news, Telegram)
  Services/               MarketDataService - the ACTUAL data path the live endpoints use
  Strategy/               The real structure/strategy engine - see STRATEGY.md
  Program.cs              Minimal API endpoints

backend.Tests/            xUnit tests for Strategy/ - 76 tests, deterministic fixtures only

frontend/                 Angular 18 app (standalone components)
  src/app/app.component.ts   Ported from the original static site's app.js;
                              plain imperative DOM rendering, not Angular templates,
                              to keep markup/behaviour identical to the original design.
```

## Two parallel engines - this is the most important thing to understand

There are **two separate signal-computation paths** in this codebase right now:

1. **The live path** (`Services/MarketDataService.cs` + `SignalEngine.cs`, called from
   `Program.cs`'s `/api/setups*` endpoints). This is what the frontend actually
   displays. It's a v1 heuristic: EMA20/EMA50 trend filter + ATR-based stop/target,
   fixed 2R. It is explicitly labeled "unvalidated v1" in its own output.

2. **The real strategy engine** (`Strategy/*.cs`). Non-repainting pivots, BOS/CHoCH,
   Market Condition classification, FVGs, order blocks, premium/discount, the
   Realtouch Willis Zone, liquidity sweeps, the four setup models, entry/stop/target
   calculation, the 100-point confluence scorer, risk/position sizing, and the
   signal lifecycle state machine. Built and tested against `backend.Tests/`, but
   **not called by any live endpoint yet**.

Wiring #2 to replace #1 is the largest remaining piece of work, and it depends on
Section 7 (multi-timeframe candle fetching/alignment) being built first - the setup
models and scorer currently mark HTF-alignment requirements as `NotEvaluated`
precisely because that fetch doesn't exist yet.

## Why two engines instead of building #2 directly into the live path

The live UI needed to keep working (real prices, real charts) while the much larger
structure-detection engine was built incrementally and tested in isolation. Cutting
over the live endpoints to the untested/half-finished engine at any intermediate
point would have meant either breaking the working app or quietly shipping unproven
logic as if it were live - both worse than the current explicit split.

## Data flow (live path, what's actually running)

```
Frontend (app.component.ts)
  --5min poll--> GET /api/setups/crypto  --> MarketDataService.FetchBybitCandles
  --60min poll-> GET /api/setups/fx      --> MarketDataService.FetchTwelveDataCandles (paced, key-rotated)
                                          --> SignalEngine.Compute (EMA/ATR heuristic)
                                          --> SetupSnapshot JSON
```

## Data flow (strategy engine, tested but not wired)

```
NormalizedCandle[] (per timeframe)
  --> PivotDetector --> StructureAnalyzer (BOS/CHoCH) --> MarketConditionClassifier
  --> FvgDetector, OrderBlockDetector, PremiumDiscount, WillisZoneDetector, LiquiditySweepDetector, KeyLevelCatalog
  --> SetupModels.Evaluate* (4 models, requirement checklists)
  --> EntryStopTargetCalculator --> ConfluenceScorer --> RiskSizing
  --> SignalLifecycle (pure state transitions)
  --> SignalResultBuilder --> SignalResult (section 27 shape)
```

## Secrets

All third-party API keys live in `dotnet user-secrets` (see `ENVIRONMENT.md`),
never in the repository or a committed file. `.env` exists locally as a scratch
reference only and is gitignored; `.env.example` lists variable names with no
values.

## Known architectural gaps (see STRATEGY.md for the full list)

- No persistence layer for signal lifecycle state, key-level catalogs, or
  historical candle storage - everything is computed fresh per request from
  whatever candles the provider returns.
- No chart-overlay rendering tied to the real Strategy/ objects - the frontend's
  chart overlay is still hand-positioned CSS/SVG divs driven by the v1 heuristic's
  numbers.
- No backtesting harness.
