# Realtouch Smart Trade

A paper-trading signal engine for crypto and forex. It detects Smart Money
Concepts (SMC) setups on real market data, scores and gates each one against
an explicit rule set, simulates how the trade would actually fill, tracks it
to a real outcome, and posts qualifying setups to Telegram. **It never places
a real order.** Every trade in the ledger is simulated.

## What it actually does

- **Four independent strategy models** - Trend Continuation Pullback,
  Breakout and Retest, Liquidity-Sweep Reversal, Range Boundary Rejection -
  each evaluated on every scan with its own mandatory gates and its own
  100-point weighted score (never a first-match shortcut, never one shared
  scoring profile).
- **Common safety gates** applied on top of every model: at least five
  independent confluence families scored, and round-trip cost capped at 25%
  of the risk.
- **Market Condition routing**: a model only qualifies in the conditions it's
  meant for (see `STRATEGY.md` for the table). An unresolved condition
  qualifies nothing.
- **A real, simulated fill**: spread, slippage and commission are charged,
  partial exits at TP1/TP2/TP3 are weighted correctly, a stop and a target
  inside the same candle are resolved from finer candles where available or
  flagged uncertain, and every trade's net R, gross/net pips or points,
  MFE/MAE and closure reason are recorded.
- **A paper ledger and dashboard**: every qualifying, triggered setup becomes
  a tracked trade. The dashboard shows live setups, the resolved-trade
  tracker (with Wilson confidence intervals, not raw win rate), a
  diagnostics view of every model's evaluation on every scan, and a daily
  rollup.
- **A backtester** that replays the exact same engine - not a second
  implementation - over real historical candles with no look-ahead, reports
  walk-forward folds and an in/out-of-sample split, and is the only
  legitimate way to judge the strategy before trusting it with real sample
  sizes.
- **Four shadow-mode strategies** (Session Liquidity Sweep, Volatility
  Contraction Breakout, Zone Mitigation, Failed Breakout and Reclaim) that
  are evaluated and recorded on every scan but never alerted and never
  entered into the ledger, pending their own backtest validation.

## Markets covered

| Market | Pairs | Data source |
|---|---|---|
| Crypto | BTC/USDT, ETH/USDT, CAKE/USDT, SOL/USDT | Coinbase public API (keyless) |
| Forex | EUR/USD, GBP/USD, GBP/JPY | Twelve Data (free key) |
| Metals | XAU/USD | Twelve Data (free key) |

See `DATA_SOURCES.md` for the full provider list, including the economic
calendar and news sources.

## Telegram alerts

- Only sent for a setup that is grade B or better **and** actually
  triggered (price has traded into the entry zone with a matching structure
  event) - a qualifying setup that hasn't triggered yet is real but not yet
  a position.
- **15-minute setups never alert** - they are on the dashboard only, for
  both the initial signal and its later TP/stop updates.
- **Forex alerts pause from Friday 22:00 UTC to Sunday 22:00 UTC.** Scanning
  itself keeps running all week so the dashboard stays current; only the
  Telegram message is held.
- A setup whose grade moves (e.g. B to A+) while its entry and direction
  stay the same is sent again, labelled "Grade changed", rather than
  suppressed as a duplicate.
- After a trade closes, the same symbol, timeframe and direction has a
  4-candle cooldown before it can alert again.
- The economic calendar and news state are shown for context only - they
  never affect whether a signal qualifies, except a genuine high-impact
  calendar event, which does veto a signal outright.

## Honesty about current performance

This is paper trading, accumulating real (simulated) trades over time. At
any given point the sample size is almost certainly still small - treat
anything under 30 resolved trades for a given model, symbol or timeframe as
provisional, and under 50-100 as too early to use for changing any rule.
Check the live tracker's Diagnostics tab and the backtester's own output
for current numbers rather than trusting any number written down here, since
it goes stale the moment more trades resolve. See `VALIDATION.md` for what
has and hasn't been independently verified.

## Running it

```bash
# Development server (frontend polls the live backend)
dotnet run --project backend

# One-shot scan (what the scheduled GitHub Action actually runs)
dotnet run --project backend -- --scan-once

# Backtest a pair's live-engine rules over real history
dotnet run --project backend -- --backtest BTC/USDT 1H 60

# Backtest the shadow-mode strategies the same way
dotnet run --project backend -- --backtest-shadow BTC/USDT 1H 60

# Frontend
cd frontend && npm install && npx ng serve
```

Backend tests: `dotnet test backend.Tests`.

## Deployment

A scheduled GitHub Action (`.github/workflows/scan-and-deploy.yml`, every 15
minutes) runs one scan, sends any Telegram alerts, persists the calendar/
news/alert-dedup/diagnostics caches to a `bot-state` branch so the next run
warm-starts instead of spending provider quota from zero, builds the
frontend and publishes `signals.json`, `signal-log.json`, `diagnostics.json`,
`reconciliation.json` and `shadow-candidates.json` alongside it to GitHub
Pages. The published site reads those files directly; there is no always-on
backend behind it.

## Further documentation

- **`STRATEGY.md`** - the actual strategy engine: every model's gates and
  scoring table, the common gates, Market Condition routing, fill
  integrity, the backtester, and a running log of what's shipped and why.
  This is the most current and most detailed doc in the repo.
- **`DATA_SOURCES.md`** - every external provider, what it's used for, and
  its real limitations.
- **`ENVIRONMENT.md`** - required secrets (local and GitHub Actions) and local setup.
- **`ARCHITECTURE.md`** - the actual code layout and data flow, including what's
  legacy/unused.
- **`VALIDATION.md`** - what's actually been tested and, just as importantly,
  what hasn't (no FX backtest, no simulated calendar/news in the backtester,
  grade is not a win-probability).

This system does not execute real trades and is not financial advice.
