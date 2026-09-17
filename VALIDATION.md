# Validation

## What has been validated

**76 automated xUnit tests** (`backend.Tests/`, run with `dotnet test`), all
passing, using deterministic synthetic candle fixtures (zig-zag uptrends/
downtrends, ranging sine waves) - never live market data, so results are
reproducible on every run.

Coverage by module:

| Module | What's tested |
|---|---|
| `CandleQualityChecker` | Duplicate/gap/stale detection, `HasUsableData` |
| `PivotDetector` | Confirmed-pivot timing, non-repainting (pivot never usable before its confirmation candle closes) |
| `StructureAnalyzer` | BOS on trend fixtures, CHoCH on reversal fixtures, every event's referenced pivot confirmed before the event's own candle time |
| `MarketConditionClassifier` | Trending/Ranging/insufficient-history classification |
| `Displacement` | Large-directional-candle detection vs. ordinary candles |
| `FvgDetector` | Gap detection, ATR-width threshold, full mitigation lifecycle |
| `OrderBlockDetector` | Detection tied to confirmed BOS/CHoCH, distal-boundary validity |
| `PremiumDiscount` | Range building, zone classification, retracement-fraction thresholds |
| `WillisZoneDetector` | FVG-overlap and retracement-overlap requirements |
| `LiquiditySweepDetector` | Full three-part confirmation (breach + close-back + follow-through), correctly ignores an unrelated direction's sweep |
| `SetupModels` | Precondition gating per model, and specifically that **no candidate can report `Qualified: true`** while entry/stop/target math and HTF alignment are unbuilt |
| `KeyLevelCatalog` | Previous-day/week levels only on matching timeframe, equal-level clustering, range equilibrium placement, invalidation |
| `EntryStopTargetCalculator` | Zone selection, stop below/above entry correctly by direction, target ordering, rejection of an irrationally tight stop |
| `ConfluenceScorer` | Best-case ≈ A+, worst-case = 0/"No setup", every family capped at its own maximum, session/news/macro always 0 and labeled why |
| `RiskSizing` / `PortfolioSafeguards` | Hard 2% cap enforced even when a higher risk is requested, fee/slippage haircut reduces size, all five safeguard checks (aggregate risk, daily loss, max positions, duplicate signal, correlated exposure) |
| `SignalLifecycle` | Every transition in the happy path, expiry, invalidation, stop-out, and that terminal states never advance further |
| `SignalResultBuilder` | One full end-to-end assembly from raw candles through to the final response shape, on a real (fixture) uptrend |

## What has NOT been validated

- **No backtesting** (section 28) exists. No win rate, expectancy, profit
  factor, drawdown, or any other historical performance number has been
  computed for this strategy, on any market, ever. Nothing in this codebase
  should be read as evidence the strategy has an edge.
- **No live signal has been produced end-to-end** against real market data -
  the tested pipeline above runs entirely on synthetic fixtures. The live
  endpoints (`/api/setups*`) still run the separate, simpler v1 heuristic
  (see ARCHITECTURE.md) - the two have not been cross-validated against each
  other or against real data.
- **No walk-forward or out-of-sample testing** - there is no historical
  dataset wired into this project at all yet.
- Real-world edge cases not covered by the synthetic fixtures (extreme
  gaps, halts, exchange outages producing partial candles, etc.) are
  untested.

## How to actually validate this before trusting any output

1. Do not treat `SetupQualityScore` or `Grade` as a probability of winning -
   the spec itself says the same. It is a checklist of internal
   consistency, not a proven edge.
2. Before any real backtesting can happen, this needs: a historical candle
   dataset per instrument/timeframe, a backtest harness reusing these exact
   `Strategy/` functions (never a separate reimplementation, to avoid
   look-ahead drift), and realistic cost modeling (spread/commission/
   slippage) - none of which exist yet.
3. Treat every commit to this engine as "logic verified to behave as
   designed on synthetic data," not "verified to make money."
