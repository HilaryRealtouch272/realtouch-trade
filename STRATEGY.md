# Strategy — Implementation Status

This file describes exactly what is implemented in code today. Where the full
master spec's rules are not yet built, they are listed under "Not yet
implemented" rather than described as done.

## Implemented (backend/Strategy/, backend/Providers/)

### Normalized candle schema
`Models/NormalizedCandle.cs` — canonical symbol, provider symbol, provider
name, timeframe, open/close time (UTC), OHLCV, `IsComplete`, `ReceivedAtUtc`,
`Quality` (Ok/Stale/Gap/Duplicate/OutOfOrder).

### Data quality (`Providers/CandleQualityChecker.cs`)
- Duplicates (same open time) are dropped.
- A gap is flagged when the time between consecutive candles exceeds 1.5×
  the timeframe's expected duration.
- The most recent candle is flagged `Stale` if it's older than 2.5× the
  timeframe duration relative to "now".
- `HasUsableData` refuses to proceed on a stale or insufficient series.

### Non-repainting pivots (`Strategy/Pivot.cs`)
Confirmed swing high/low requires the configured number of candles on
**each side** with a strictly less-extreme high/low (Section 5 bar counts:
Weekly 2, Daily/4H/1H/15m 3). A pivot's `ConfirmedAtUtc` is the close time of
the last required right-side candle — the structure engine never references
a pivot before that timestamp.

### BOS / CHoCH (`Strategy/StructureAnalyzer.cs`)
Walks completed candles in chronological order, only surfacing a pivot once
confirmed at that point in time (no look-ahead):
- **Bullish BOS**: close > confirmed swing high + 0.10×ATR(14).
- **Bearish BOS**: close < confirmed swing low − 0.10×ATR(14).
- **Bullish CHoCH**: same break, but the prior trend state was Bearish
  (i.e. the broken high was a "protected lower high").
- **Bearish CHoCH**: symmetric.
- A wick alone never qualifies — only a completed candle's close.

### Market Condition (`Strategy/MarketCondition.cs`)
Label is **"Market Condition"** (never "Regime"), one of: TrendingBullish,
TrendingBearish, BreakoutBullish, BreakoutBearish, ReversalDeveloping,
Ranging, NeutralOrTransition.

- **Trending**: confirmed structural trend (from StructureAnalyzer) + EMA20
  vs EMA50 alignment + positive/negative EMA20 slope + ADX(14) ≥ 20.
- **Breakout**: a recent BOS whose breaking candle has body ≥ 60% of range
  and clears the pivot by ≥ 0.15×ATR(14).
- **Reversal Developing**: a recent CHoCH meeting the same displacement bar.
- **Ranging**: ADX < 20 and no one-directional run of recent BOS/CHoCH.
- Anything else, or fewer than 60 completed candles available: Neutral/Transition.

### Indicators (`Strategy/Indicators.cs`)
EMA, Wilder ATR(14), Wilder ADX(14) — all causal (no look-ahead), used to
*support* structure, never to generate a signal alone (Section 4).

### Displacement (`Strategy/Displacement.cs`)
Section 10: body ≥ 1.5× the median body of the previous 20 completed
candles, body ≥ 65% of the candle's own range, and close within the outer
third of the range on the directional side. Used to validate order blocks
and the Willis Zone — never a signal on its own.

### Fair Value Gaps (`Strategy/FairValueGap.cs`)
Section 11: classic 3-candle imbalance (third candle's low/high clears the
first candle's high/low), gated on width ≥ 0.10×ATR(14) so sub-threshold
gaps never count as confluence. Tracks upper/lower/midpoint and walks
forward candle-by-candle to classify mitigation as Unmitigated →
PartiallyMitigated → FullyMitigated, or Invalidated if a candle's *close*
breaks through the gap's far boundary.

### Order blocks (`Strategy/OrderBlock.cs`)
Section 12: for every confirmed BOS/CHoCH from `StructureAnalyzer`, walks
backward to the last opposite-coloured candle before that break. Bullish →
zone is candle open-to-low; Bearish → open-to-high. Tracks whether price has
since closed through the distal (far) boundary (`IsValid`) and a
timeframe-scaled max age in bars.

### Premium / Discount (`Strategy/PremiumDiscount.cs`)
Section 13: builds a dealing range from the impulse behind the most recent
structure event, computes equilibrium, and classifies any price as
Discount/Equilibrium/Premium plus a retracement fraction against the
preferred (62–70.5%) and tolerated (50–79%) pockets.

### Realtouch Willis Zone (`Strategy/WillisZone.cs`)
Section 14: takes each valid, freshness-and-width-qualified order block,
requires it to fall in the tolerated retracement band **and** overlap a
same-direction FVG before it counts as a Willis Zone. (Criterion 6 —
"followed by liquidity and structure confirmation before entry" — is an
entry-time condition for the setup models to enforce, not a detection-time
property, so it's intentionally not checked here.)

### Liquidity sweeps (`Strategy/LiquiditySweep.cs`)
Section 9: a confirmed pivot must be breached by at least 0.05×ATR(14), the
same completed candle must close back on the correct side of it, **and**
displacement or a matching-direction structure event must follow within 5
candles. Anything not meeting all three is not returned at all — per spec,
an unconfirmed break is a breakout attempt, not a sweep.

### Setup models (`Strategy/SetupModels.cs`) — partial, precisely scoped

Section 15's four models (`EvaluateTrendContinuationPullback`,
`EvaluateBreakoutAndRetest`, `EvaluateLiquiditySweepReversal`,
`EvaluateRangeBoundaryRejection`) are implemented as **requirement
checklists**, not full signal generators:

- Each method returns `null` if the model's Market Condition precondition
  doesn't hold (the model doesn't apply), or a `SetupCandidate` listing
  every requirement it checked.
- Every requirement is recorded as `Met`, `NotMet`, or `NotEvaluated` —
  never silently assumed. **Multi-timeframe (Weekly/Daily/4H) directional
  alignment** remains `NotEvaluated` on every model, because fetching and
  aligning candles across multiple timeframes for the same symbol isn't
  wired yet (Section 7 - single-timeframe analysis only, so far).
- **Entry/stop/target math now exists** (`EntryStopTargetCalculator`, see
  below) as a separate downstream step, but `SetupModels.cs`'s own "Minimum
  2:1 reward-to-risk" requirement has **not yet been updated to consume
  it** - it is still hardcoded `NotEvaluated`. This is a real, known wiring
  gap, not a missing capability: the R:R number is computable today, the
  setup-model checklist just doesn't ask for it yet.
- `SetupCandidate.Qualified` is `true` only if every checked requirement is
  `Met` **and** none are `NotEvaluated`. Given the two gaps above, **no
  candidate can currently report `Qualified: true`** — enforced by a test
  (`SetupModelsTests.TrendContinuationOnATrendingMarketNeverReportsQualifiedYet`).
- Simplifications inside what *is* checked, stated explicitly: "clear
  rejection" (Model A) is approximated as sweep-or-zone-interaction; Model
  D's range boundaries are a rolling 20-bar high/low rather than a formally
  detected range structure; Model B's "not chasing" check uses a fixed
  3×ATR distance rather than a structure-derived limit.

### Key levels (`Strategy/KeyLevel.cs`)
Section 8, as a discrete stored catalog (not just raw pivots): previous
week/previous day high/low (only meaningful, and only computed, when the
series' own timeframe IS that period - true cross-timeframe "previous day
high while viewing 4H" needs Section 7's multi-timeframe fetch), confirmed
swing highs/lows (with mitigation/invalidation/reaction-count tracking),
equal highs/lows (pivot clustering within 0.15% tolerance), and a rolling
20-bar range high/low/equilibrium. **Not included**: session highs/lows
(Asian/London/NY) - accurate session bucketing needs proper IANA/DST-aware
per-candle time handling, not yet built.

### Entry / Stop / Target (`Strategy/EntryStopTarget.cs`)
Sections 16-18. Given a direction and the detected zones/sweeps/key levels:
- **Entry**: prefers a Willis Zone, then a plain order block, then a bare
  FVG; preferred entry is the zone's midpoint (per spec rule 5); "Triggered"
  requires both price being in the zone AND a matching-direction structure
  event at/after the zone's origin; expires 10 candles after the zone forms.
  `EntryTimeframe` is currently the **same** timeframe the setup was
  detected on - the true context-TF/entry-TF split from Section 7's
  hierarchy table isn't wired yet, stated explicitly in the type's doc comment.
- **Stop**: nearest structural invalidation (order-block distal boundary or
  liquidity-sweep level, whichever is closer) plus a 0.10×ATR(14) buffer.
  Rejected outright (`Compute` returns `null`) if the resulting distance is
  irrationally tight or wide relative to ATR - tested.
- **Targets**: TP1 ≈ 1R; TP2 is the nearest opposing key level beyond 1R,
  else a flat 2R; TP3 is the next key level beyond TP2, else 3R. Fixed
  25/50/25 scaling per the spec's default.

### Confluence scoring (`Strategy/ConfluenceScore.cs`)
Section 19's 100-point, 8-family model, implemented and tested against
best-case (≈95, grade A+) and worst-case (0, "No setup") inputs. The
**Session/news/macro family always scores 0**, explicitly labeled "not
wired" in its own basis string, rather than silently omitted - Sections
21-23 don't feed into it yet. HTF-context scoring is a documented
**single-timeframe proxy** for the same Section 7 reason as above.

### Risk & position sizing (`Strategy/RiskSizing.cs`)
Section 20. Default risk by grade (B 0.5%, A 1%, A+ 1.25%), hard-capped at
2% regardless of what's requested. Position size = risk amount ÷ price
distance, with an optional fee/slippage haircut. **Correct for same-
account-currency instruments only** - true FX cross-currency pip-value
conversion (e.g. a GBP account trading EUR/USD) is not implemented and is
stated as such in the result. `PortfolioSafeguards` implements all five
spec safeguard checks (aggregate risk, daily loss, max simultaneous
positions, duplicate signal, correlated exposure) as pure functions over
caller-supplied state - **no persistence exists yet to track real open
positions**, so nothing calls these with live state today.

### Signal lifecycle (`Strategy/SignalLifecycle.cs`)
Section 26's full state machine (Scanning → Watchlist → Armed → Triggered →
Active → TP1/TP2/TP3 → terminal states), as pure transition logic - given a
current state, a `TradePlan`, and a price, it returns the next transition
or `null`. Terminal states never advance further (tested). **No persistence
layer** stores this state across calls/restarts yet - that's a separate
infrastructure decision (which database/table) not made.

### Full API response shape (`Models/SignalResult.cs`)
Section 27's complete per-setup field list, plus `SignalResultBuilder`
which assembles one from all of the above. Proven with an end-to-end test
that runs raw candles through the entire pipeline to a final `SignalResult`.
**Not returned by any live endpoint** - that needs Section 7's
multi-timeframe orchestration to fill the cross-timeframe fields honestly.

### Tests (`backend.Tests/`)
**76 passing xUnit tests**, all on deterministic synthetic fixtures (no
live data): everything listed above, plus an end-to-end assembly test
proving the full pipeline composes correctly, not just each piece in
isolation.

### Multi-timeframe hierarchy & live orchestration (`Strategy/TimeframeHierarchy.cs`, `Services/SignalOrchestrator.cs`)
Section 7's context/entry-timeframe mapping table, plus a real HTF-alignment
verdict (Aligned/Neutral/Conflicting) computed from **actual fetched
candles** on the context timeframes - not a stub. `SignalOrchestrator` wires
the entire engine together for the first time into one live, callable path:
fetch → structure → condition → zones/sweeps/key levels → fetch context
timeframes → HTF alignment → setup models (now genuinely evaluated, not
permanently `NotEvaluated`) → entry/stop/target → confluence score → risk
sizing → lifecycle → `SignalResult`.

**Live at `GET /api/signals/crypto`** - the four Bybit instruments across
all five timeframes, using real Bybit data end to end. This is the first
endpoint in the project actually running the real Strategy engine rather
than the v1 heuristic. **Not yet tested against live data** (no network
access from the environment that built it) - needs a real run to confirm.

**Also live at `GET /api/signals/fx`** - extended to the 4 Twelve Data
instruments (EUR/USD, GBP/USD, GBP/JPY, Gold) across all 5 timeframes, at
the user's explicit request accepting the quota cost. Each evaluation makes
up to 3 sequential Twelve Data calls (main + up to 2 context timeframes),
and evaluations run one at a time with a 3-second gap - a full scan is ~20
evaluations, ~44 API calls, spread over roughly a minute, round-robined
across all configured keys. The provider layer (`Providers/TwelveDataMarketDataProvider.cs`)
now carries the same quota-safety fixes as the older `/api/setups/fx` path
(known-unavailable-symbol skip, round-robin key start, fail-fast on
non-key-specific errors) - it lacked them until this pass.
**Not polled automatically by anything** - it only runs when a request
hits it directly, so there's no recurring quota drain yet. Wiring it into
the frontend's polling loop is a separate decision.

**Context-timeframe caching**: `SignalOrchestrator` now caches each
(provider, symbol, context-timeframe) condition with a TTL scaled to that
timeframe's own candle duration (half the duration, minimum 10 minutes -
e.g. Daily caches for 12h, H4 for 2h). This means once a timeframe's slow
context has been fetched once, repeated scans of a *fast* timeframe (15m,
1H) mostly only re-fetch their own main candles - the expensive HTF context
calls aren't repeated every cycle. Both `/api/signals/crypto` and
`/api/signals/fx` accept an optional `?timeframe=15m` (etc.) query param so
the frontend can poll different timeframes at different cadences instead of
one blanket interval for everything - e.g. 15m every ~10 min, Weekly once a
day. **Not unit-tested** (would need injectable time/clock abstraction to
test TTL expiry deterministically - not done yet, noted as a gap).

`SetupModels.cs`'s HTF-alignment and R:R requirements now accept real
values via optional parameters (`htfAlignment`, `rewardToRisk`) - existing
callers that don't pass them keep the old, honest `NotEvaluated` behavior
(backward compatible, still tested).

### Economic-calendar veto & news catalyst (`Strategy/EconomicCalendarVeto.cs`, `Strategy/NewsCatalyst.cs`)
Sections 21-22, now genuinely wired end to end, not just adapters sitting
unused. `EconomicCalendarVeto.Evaluate` checks real fetched events against
each instrument's affected currencies (`InstrumentDefinition.AffectedCurrencies`)
with the spec's exact veto windows (60min/30min for central-bank/CPI/employment,
30min/15min for other high-impact, a non-blocking "Caution" for medium-impact
within an hour). `NewsCatalystEvaluator.Evaluate` averages sentiment across
sufficiently-relevant news items and returns Aligned/Mixed/Conflict/Unchecked/
Unavailable relative to the setup's direction - it never lets news
manufacture a signal, only judge one already produced by structure.

**A `HardVeto` now genuinely blocks a signal** - `SignalOrchestrator` checks
it before returning a result, and `SetupModels`' "No active hard news veto"
requirement (added to all four models) means `Qualified` can never be true
while one is active, tested explicitly.

**Quota discipline, given Alpha Vantage's free tier is 25 requests/DAY per
key** (far tighter than Twelve Data): the calendar is fetched **once
globally** (not per-instrument) and cached for 1 hour, filtered locally per
instrument by currency. News is cached per-instrument-symbol for 2 hours;
the direction-relative verdict is re-derived locally from cached raw items
each time rather than re-fetched, since sentiment data doesn't change just
because the setup's direction did.

**Fixed the same day it was found**: `ConfluenceScorer`'s "Higher-timeframe
context" family previously scored off the setup's own single-timeframe
condition only, even after `SignalOrchestrator` started computing a real
multi-timeframe `HtfAlignment` verdict for the qualification checklist - the
score and the checklist could disagree. `ScoringInput` now carries
`HtfAlignment`, `CalendarVeto`, and `NewsCatalyst`, and a real `Conflicting`
HTF verdict zeroes that family outright regardless of the single-timeframe
match, tested explicitly.

**`SetupModels.cs`'s last three permanently-`NotEvaluated` requirements are
now real checks**, closing the model-completeness gap flagged in the
"Setup models" section above:
- LiquiditySweepReversal's **"Entry on the controlled retest"** now checks
  whether price has actually come back to the FVG/order block the reversal
  displacement left behind (ATR-scaled tolerance), reusing the same
  `NearAnyZone` helper `BreakoutAndRetest` already used for its own retest
  check - rather than accepting entry on the CHoCH candle itself.
- LiquiditySweepReversal's **"Reduced risk when against the broader Weekly
  trend"** now reports whether that reduction has genuinely been applied:
  `SignalOrchestrator` halves the position size when this model's HTF
  alignment comes back `Conflicting`, real behavior, not just a label.
- RangeBoundaryRejection's **"Logical target at equilibrium or opposite
  boundary"** now validates the real computed target (threaded through on
  Pass 2, the same pattern already used for R:R) against the range's own
  equilibrium/opposite boundary rather than an arbitrary ATR extension.

Note: none of the four models' own `Qualified`/requirements-checklist flags
gate live signal issuance - that runs entirely through `ConfluenceScorer`'s
model-agnostic `ScoringInput` (HTF alignment and R:R are scored for every
model uniformly, regardless of which specific sub-items that model's own
checklist happens to enumerate). This checklist is diagnostic/display data
(and a real behavioral input to the two changes above), not a live gate.

## Multi-Strategy Scoring Enhancement and Pip Performance Engine (2026-09-22)

Audit requested first: only Trend Continuation Pullback was ever qualifying.
Root causes found by tracing the actual selection path, not by guessing:

1. `TryModels` was a first-match chain (`Trend ?? Breakout ?? Reversal ??
   Range`) - a qualifying Trend candidate silently prevented the other
   three from ever being tried, and a rejected candidate left no record at
   all (audit items: early-return logic, rejected candidates not stored).
2. Breakout and Reversal detection both keyed off a single structure event
   - either the classifier's current-instant Market Condition label (which
   only reports `BreakoutBullish/Bearish` for ~3 candles after the actual
   break) or the literal last structure event overall (for Reversal's
   CHoCH check). A real retest, or a real post-CHoCH entry, that developed
   even slightly later than that narrow window was silently undetectable -
   this is the actual "entry-trigger logic that cannot be satisfied by
   other strategies" the audit asked about.
3. The shared `ConfluenceScorer` only credited "premium/discount location"
   (15 pts) and structural HTF-context-match to models whose own
   requirements could describe them in those exact terms - Range Boundary
   Rejection topped out around 70/100 against a 75 threshold regardless of
   setup quality (confirmed: incorrect score normalisation for non-trend
   models, not inappropriate global rules or volume penalties - the
   strategy code never reads volume at all, and all 5 timeframes were
   already being scanned for every instrument).

Fixed:

- **`Strategy/SetupModels.cs`**: `EvaluateBreakoutAndRetest` and
  `EvaluateLiquiditySweepReversal` now search a 30-candle window (via
  `Strategy/ModelEvidence.cs`) for the most recent matching event, not the
  classifier's current-instant label or the literal last event. Two
  regression tests reproduce the exact failure mode - a real CHoCH/breakout
  with a later, unrelated structure event is still detected.
- **`Services/SignalOrchestrator.cs`**: all four models now evaluate
  independently every scan (`EvaluateOneModel` looped over `AllModels`, no
  early return), and every evaluation - qualified or not - is recorded via
  `Services/StrategyDiagnosticsStore.cs`. The highest-scoring qualified
  model becomes the tracked trade; the rest are diagnostics only, never a
  second trade for the same symbol+timeframe (this preserves the existing,
  explicitly-chosen one-open-trade rule rather than tracking every
  simultaneously-qualifying model as a separate position).
- **`Strategy/ModelScoring.cs`**: each model scores against its own 100-point
  weight table (sections 6.1-6.4, weights transcribed exactly) and its own
  threshold (75/75/78/78). Grade banding is per-model, not fixed: a 76 on a
  threshold-78 model is `Tracking`, not `B`, per section 11's explicit rule
  - verified by test. `Strategy/ModelEvidence.cs` computes the graded
  evidence each table needs (breakout close-body ratio, ATR-scaled sweep
  quality, range containment ratio) from the same detector outputs already
  computed once per scan - no duplicate structure/order-block/FVG
  detection.
- Fixed a real scoring bug found during the rewrite: `EntryStructureEvent`
  used the literal last structure event of *any* direction, so a Range
  short could be credited a bullish CHoCH. Now matched to the candidate's
  own direction.
- **`Strategy/RangeTradePlan.cs`**: Range Boundary Rejection gets its own
  trade plan - target is the range's own equilibrium/opposite boundary
  (section 6.4), not the pullback planner's order-block/FVG logic, which a
  bare range boundary usually doesn't have sitting on it.
- **`Models/InstrumentMetadata.cs` + `Strategy/PipCalculator.cs`** (sections
  13-14): central pip/point config per symbol (GBP/JPY's pip is the 2nd
  decimal, 0.01, not the 4th; crypto reports quote-currency points, never
  "pips"), decimal-safe gross/net movement matching the brief's exact
  Long/Short formulas and its section-15 partial-exit worked example.
- **`Strategy/WilsonInterval.cs`** (section 12): 95% Wilson score interval +
  the exact sample-size bands from the brief. Verified against the brief's
  own baseline (7 trades, 4 wins, 57.14%): the true win rate could honestly
  be anywhere from ~25% to ~84%. Wired into the frontend Performance tab
  with a visible Provisional banner under 30 resolved trades.
- **`/api/strategy-diagnostics`** (+ `/raw`): per-model detected/qualified/
  rejected counts, average score, near-miss count (within 5 points of
  threshold), top rejection reasons - section 9's diagnostics view, as an
  API for now (see "Not done" below for the dedicated UI).

**A real, important finding, not just a code fix**: the "4 TP3 winners, 3
stops, 57.14%" baseline cited at the start of this work was checked against
this session's `Triggered`-gating fix (already shipped earlier the same
day, see the "Fix mass-expiry..." and "Don't log or alert a trade as live"
entries above) and against real market data cross-referenced on
TradingView. Both of the only two trades ever logged (including the one
this baseline was built from) were entered without price ever actually
trading into the stated entry zone - the ledger was reset to empty as a
result. **The 7-trade baseline in the brief does not exist in the current,
corrected ledger.** Section 12's own instruction - do not optimise around a
small, unverified sample - applies doubly here.

**Completed in the follow-up pass (same day)**, closing out everything
listed as "Not done" above at the time:

- **Per-trade record (section 17)**: `QualificationLogEntry` now carries
  the full field set - `StrategyVersion`, `AssetClass`, `MarketCondition`,
  `RiskPercent`/`RiskAmount`/`PositionSize`, `EntrySpreadUnits`/
  `SlippageUnits` (seeded automatically from `InstrumentMetadataCatalog` at
  creation, closing the "instrument-specific spread/slippage defaults" gap
  too), running `MaxFavorableExcursionR`/`MaxAdverseExcursionR` updated on
  every live price check (not just at closure), and a full closure-time
  set - `Tp3HitAtUtc`/`StopHitAtUtc`, gross/cost/net movement in the
  instrument's own pip/point unit (via `PipCalculator`, reusing the
  already-correct blended `RealizedR` rather than re-deriving a second pip
  calculation), `MonetaryPnL`, `PercentageReturn`, a `FinalOutcome`
  category (TP3 Win / TP2 or TP1 Partial Win / Stopped Out / Expired Flat /
  Breakeven), `ClosureReason`, and `HoldingDurationHours`. All new fields
  are appended with defaults - no existing call site or persisted row
  breaks. The frontend ledger now shows net pips/points alongside the
  R-multiple on every resolved row. This also means spread/slippage cost
  is now genuinely subtracted from a trade's net movement at closure,
  partially addressing section 16's fill-integrity intent (see the
  remaining gap below).
- **Dedicated diagnostics UI**: a third "Diagnostics" tab on the Signal
  Tracker now renders `/api/strategy-diagnostics` - per-model scan/
  detected/qualified/rejected counts, average score, near-miss count, and
  top rejection reasons. Verified against a live local backend before
  committing (curled the endpoint and cross-checked the response shape
  against the render code). Local-backend only, like the endpoint itself;
  the static deployment shows an honest "not available" message rather
  than pretending to have data it doesn't.
- **Walk-forward / period reporting (section 12)**: the tracker's
  Performance tab now includes a chronological (not count-sorted) breakdown
  by ISO calendar week, computed from real accumulated paper-trading
  results. Explicitly labeled in the UI as **not** a historical bar-replay
  backtest - no historical OHLC ingestion pipeline exists to run one
  against, so nothing is simulated against past candles; every row already
  happened as a real live paper trade when it was logged. True
  out-of-sample backtesting (section 28) remains unbuilt - see below.

**Still not done** (explicit, not silently skipped):

- **Same-candle target/stop sequencing (section 16)**: the live engine
  checks price once per scan against real current price, not once per
  candle against an OHLC bar's internal path - there is no "target and stop
  both inside the same candle" ambiguity to resolve in this architecture
  the way a bar-by-bar backtest would have it, since it isn't replaying
  historical bars. Spread/slippage cost is now subtracted at closure (see
  above), but nothing yet models an actual same-candle fill-order decision.
- **Historical backtesting / true out-of-sample testing (sections 12, 28)**:
  no historical OHLC ingestion or bar-replay backtest runner exists. The
  period-by-period breakdown above is walk-forward reporting on real
  trades, not a substitute for this - a genuinely separate subsystem that
  wasn't attempted here, flagged rather than faked.

## Universal score floor (2026-09-24, owner decision)

Any detected setup with a real trade plan (entry, stop, targets) that scores
**76 or higher** is now a tradable signal: alerted to Telegram, logged and
tracked, regardless of whether a model's mandatory gates are confirmed or its
score is under that model's own 75/78 threshold. This was requested
explicitly and repeatedly; the tradeoff it accepts is that some alerted
setups have a structural precondition the model itself did not confirm.

How it is bounded, so it stays honest and measurable:

- **Still required**: a valid trade plan (otherwise there is nothing to trade
  or track), the hard economic-calendar veto, and an entry price can
  realistically reach (see "Pending signals" below).
- **Never claimed as confirmed**: `MandatoryGatesPassed` stays false on the
  diagnostics record; a separate `ScoreFloorQualified` flag marks the
  promotion. Every such alert and ledger row carries a `ScoreFloorNote`
  naming what was unconfirmed (failed gates, unevaluated checks, or score
  under the model's own threshold).
- **Ranking**: a fully confirmed setup always outranks a floor one,
  whatever the scores.
- **Measured separately**: the tracker's Performance tab has a
  "Confirmation" breakdown (Fully confirmed vs Score-floor), so the two
  populations are never blended into one win rate. Sizing is unchanged from
  an ordinary B-grade trade.

`StrategyEvaluation.ScoreFloor` is the one constant to change if the floor
is ever revisited.

### Pending signals (2026-09-24)

A qualified signal whose price has not reached its entry yet is alerted and
logged, not held back until it triggers. It is labeled PENDING in Telegram
and tracked in the ledger as `Pending` - a limit level to wait for, not a
position.

- **Fill**: it becomes `Open` (a P&L-bearing trade) only when a candle trades
  through the entry level (a Long's Low at or below it, a Short's High at or
  above it). Only the ADVERSE extreme of the fill candle is applied, since
  which extreme came first is unknown - a favorable move is never credited
  from the candle that filled the order. Nothing before the fill counts
  toward stop, targets or excursions, ever (the earlier "TP2 hit on a trade
  that was never entered" bug). The holding window restarts from the fill.
- **Unfilled**: price reaches TP1 without touching the entry (the move was
  missed), or the tracking window expires unfilled. There was never a
  position, so there is no win, loss or R: `RealizedR` stays null, and
  Unfilled rows are excluded from win rate and average R.
- **Skipped**: a signal whose price has already gone through its entry
  without triggering is neither a limit order waiting nor a position, so it
  is not alerted or logged.
- **Reachable entries only**: a zone is eligible only if price can reach it
  within 3 ATR (about what price travels over the plan's 10-candle life).
  Before this, a live CAKE/USDT Long was planned 24% below the market -
  unreachable, and its distance inflated reward-to-risk and the score.
  TP3 is also always at least 1R beyond TP2 (it could previously land below
  it when TP2 came from a far key level).
- **Alerts**: one alert per distinct signal; the alert record is kept while
  the ledger is still tracking the signal, so a score flickering under the
  bar for one scan cannot re-send it. Fills and unfilled outcomes are
  announced by the ledger's own messages.

## Not yet implemented

- **Extending Section 7's live wiring to FX/Metals/Energy** - blocked on
  Twelve Data quota, as above.
- **A live test of `/api/signals/crypto`** against real Bybit data - the
  code is tested against synthetic fixtures only; nobody has confirmed it
  produces sane output on the actual market yet.
- **The `/api/setups`-replacing endpoint**: `/api/setups*` (what the
  frontend actually polls) still runs the original v1 EMA/ATR heuristic
  (`Services/SignalEngine.cs`). `/api/signals/crypto` is new and separate -
  the frontend doesn't call it yet.
- **Sections 21-22 live-data verification**: the logic is built and unit
  tested against fixture data, but neither Finnhub's calendar endpoint nor
  Alpha Vantage's news endpoint has been confirmed working against a real
  live call yet (no network access from the environment that built this) -
  needs a real run to confirm before trusting it, same caveat as
  `/api/signals/crypto` and `/api/signals/fx`.
- **Section 23**: volatility windows are still hardcoded session
  assumptions in the frontend, not computed from 60 days of real candle
  history.
- **Sections 24-25**: chart overlays are still hand-positioned CSS/SVG divs
  driven by the v1 heuristic's numbers, not a real chart library
  (Lightweight Charts or similar) rendering the actual `Strategy/` objects
  (FVGs, order blocks, Willis Zones, BOS/CHoCH markers).
- **Section 28**: backtesting. No historical dataset or backtest harness
  exists. See `VALIDATION.md` for exactly what has and hasn't been proven.
- Persistence for signal lifecycle state, key-level catalogs, and portfolio
  safeguard state (all currently pure/stateless functions - see
  `ARCHITECTURE.md`).
