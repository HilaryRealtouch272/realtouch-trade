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
