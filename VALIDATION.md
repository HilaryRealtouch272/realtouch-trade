# Validation

## What has been validated

**276 automated xUnit tests** (`backend.Tests/`, run with `dotnet test`), all
passing. Most use deterministic synthetic candle fixtures (zig-zag uptrends/
downtrends, ranging sine waves), so they're reproducible on every run; a
smaller set (the ledger, the alert dedupe, the reconciler) does real
construction of the actual services against an in-memory/fake HTTP layer, to
catch bugs that only show up when the pieces are wired together, not just in
each function alone.

Coverage spans: non-repainting pivot/BOS/CHoCH detection, Market Condition
classification, FVG/order-block/premium-discount/Willis Zone/liquidity-sweep
detection, all four setup models' mandatory gates and model-specific scoring,
Market Condition routing, the common gates (confluence count, cost ratio),
entry/stop/target construction (including the minimum tradeable stop and
higher-timeframe targets), fill simulation (partial exits, costs, same-candle
stop/target sequencing, net R, MFE/MAE), the paper ledger's idempotent
Telegram alerts (both the duplicate-signal and duplicate-outcome cases, and
specifically that the 15-minute/weekend Telegram gate survives the outcome
dedupe), the 1-minute crypto settlement and its restart watermark, the trade
reconciler, the news/calendar evaluators (including the 12-hour recency
window and the 3-headline minimum), the Wilson interval calculation, the
backtester's no-look-ahead candle provider and its statistics, and the
shadow-mode strategies.

## What this does NOT mean

- **A real backtest exists and has been run** - see `STRATEGY.md`'s
  backtesting section for actual results on real Coinbase history. They are
  still based on small sample sizes (single to low double digits of trades
  per pair), so treat any number there as provisional, not proof of an edge.
- **The live paper-trading ledger is the primary source of truth for
  performance**, not this file or the backtester - check the deployed
  tracker's Diagnostics tab for current, real numbers. They change every
  scan; nothing written down here should be trusted once it's gone stale.
- **No FX backtest exists** - there is no free source of deep intraday FX
  history, so FX/metals pairs have no historical validation at all, only
  live paper trades.
- **The calendar and news state are never simulated in the backtester** -
  historical calendar/news data doesn't exist for this project, so backtest
  results are somewhat more permissive than live trading would be around
  real high-impact events.
- **Grade is not a probability of winning.** It's a checklist-derived
  score, not a calibrated win-rate estimate - and the live ledger has, at
  various points, shown grade A trades performing worse than grade B on a
  small sample. That's exactly the kind of thing more trades either confirm
  or disprove; it should not be assumed either way.

## How to actually validate this before trusting any output

1. Run `dotnet run --project backend -- --backtest <SYMBOL> <TIMEFRAME> <DAYS>`
   for a crypto pair and read the output yourself - don't take a summary of
   it as gospel. It replays the real engine (`SignalOrchestrator`), not a
   separate reimplementation, so there's no look-ahead drift between what's
   tested and what's live.
2. Treat anything under 30 resolved trades for a given model, symbol or
   timeframe as provisional, and under 50-100 as too early to justify
   changing any gate or threshold (this is the project's own standing rule,
   not an external one).
3. Check the live tracker regularly rather than relying on any one
   snapshot - the sample size needed to say anything meaningful is still
   being built up in real time.
4. For FX specifically, there is no backtest to lean on - judge it purely
   from accumulated live paper trades, and expect that to take longer.
