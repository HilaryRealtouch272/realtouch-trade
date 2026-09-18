// @ts-nocheck
// Ported directly from the original static site's app.js, kept as plain
// imperative DOM code (not Angular templates/bindings) so behaviour and
// markup stay pixel-for-pixel identical. Live market data comes from the
// .NET backend's /api/signals/* endpoints (the real Strategy/ engine, not
// the old /api/setups v1 heuristic) - the backend holds the Bybit/Twelve
// Data calls and keys server-side, never exposed to the browser.
import { AfterViewInit, Component } from '@angular/core';

const API_BASE = 'http://localhost:5266';

// One row per tradeable instrument. Mirrors backend/Models/SetupDefinition.cs's
// Instruments list exactly (same baseId, same timeframe suffixes) so ids match
// and live data hydrates onto the right card.
const instruments = [
  // The former separate BTC/USD & ETH/USD "Crypto Spot" rows were removed -
  // Coinbase (the real data source since Bybit got geo-blocked on GitHub
  // Actions, see backend/Models/SetupDefinition.cs) has no perpetual-futures
  // product, so those rows were pulling the exact same real feed as these
  // USDT ones: a genuine duplicate, not a second real instrument.
  { baseId: "btc-f", symbol: "BTC/USDT", name: "Bitcoin Perpetual", group: "Crypto Futures", icon: "₿", decimals: 0, tv: "BINANCE:BTCUSDT.P" },
  { baseId: "eth-f", symbol: "ETH/USDT", name: "Ether Perpetual", group: "Crypto Futures", icon: "Ξ", decimals: 0, tv: "BINANCE:ETHUSDT.P" },
  { baseId: "eurusd", symbol: "EUR/USD", name: "Euro / US Dollar", group: "FX", icon: "€", decimals: 4, tv: "OANDA:EURUSD" },
  { baseId: "gbpusd", symbol: "GBP/USD", name: "British Pound / US Dollar", group: "FX", icon: "£", decimals: 4, tv: "OANDA:GBPUSD" },
  { baseId: "gbpjpy", symbol: "GBP/JPY", name: "British Pound / Yen", group: "FX", icon: "¥", decimals: 2, tv: "OANDA:GBPJPY" },
  { baseId: "gold", symbol: "XAU/USD", name: "Gold Spot", group: "Metals", icon: "Au", decimals: 1, tv: "OANDA:XAUUSD" },
  // Confirmed unavailable on the free Twelve Data plan/symbol set (see
  // backend/Services/MarketDataService.cs and DATA_SOURCES.md) - the backend
  // never serves data for these ids, so they're shown as "Coming soon"
  // placeholders rather than hidden, instead of being silently dropped.
  { baseId: "silver", symbol: "XAG/USD", name: "Silver Spot", group: "Metals", icon: "Ag", decimals: 2, tv: "OANDA:XAGUSD", comingSoon: true },
  { baseId: "wti", symbol: "WTI/USD", name: "Crude Oil WTI Spot", group: "Energy", icon: "WTI", decimals: 2, tv: "TVC:USOIL", comingSoon: true },
  { baseId: "brent", symbol: "BRENT/USD", name: "Brent Crude Spot", group: "Energy", icon: "BR", decimals: 2, tv: "TVC:UKOIL", comingSoon: true },
];

// Must match backend/Models/SetupDefinition.cs's TimeframeSuffix mapping exactly.
const timeframes = [
  { label: "Weekly", suffix: "w" },
  { label: "Daily", suffix: "d" },
  { label: "4H", suffix: "4h" },
  { label: "1H", suffix: "1h" },
  { label: "15m", suffix: "15m" },
];

// Every instrument gets analysis under every timeframe. All quantitative
// fields start neutral/empty (never fabricated) - hydrateSignals() (below)
// replaces them with the REAL Strategy/ engine's output via /api/signals/*,
// or an honest failure reason, once the backend responds. Nothing here is
// invented "demo" analysis. `hydrated` distinguishes "never scanned yet"
// from "scanned, no qualifying setup" - both look quiet, but only one of
// them means the backend actually looked.
const setups = instruments.flatMap(inst => timeframes.map(tf => ({
  id: `${inst.baseId}-${tf.suffix}`,
  symbol: inst.symbol, name: inst.name, group: inst.group, icon: inst.icon,
  timeframe: tf.label, tvSymbol: inst.tv, decimals: inst.decimals,
  comingSoon: !!inst.comingSoon,
  hydrated: false, stale: false, cachedAtMs: 0,
  live: false, liveSource: null, liveError: null,
  direction: "Neutral", condition: inst.comingSoon ? "Coming soon" : "Not yet scanned",
  conditionFamily: "Unscanned", grade: null,
  score: 0, rr: 0, price: 0,
  entry: 0, stop: 0, target: 0, tp1: 0, tp3: 0, updated: 0,
  levels: [["—", "—", "Awaiting live data"]],
  confluences: [],
  reasoning: inst.comingSoon
    ? "This market isn't live yet - it's waiting on a reliable free data source and will be enabled without changing its id or position once one is wired up."
    : "Awaiting live data…"
})));

const groupMeta = {
  "All Markets": { color: "#52d49c" },
  "Crypto Futures": { color: "#c78cff" },
  FX: { color: "#58d8ce" },
  Metals: { color: "#efb95d" },
  Energy: { color: "#ff8d6b" }
};

// Watchlist/comparison are meaningful user choices, not disposable UI state -
// persisted so a page reload (a dev-server hot-reload, a real refresh, a
// closed tab) doesn't silently discard them.
function loadPersistedIds(key: string): string[] {
  try {
    const raw = JSON.parse(localStorage.getItem(key) || "[]");
    return Array.isArray(raw) ? raw.filter(x => typeof x === "string") : [];
  } catch { return []; }
}
function savePersistedIds(key: string, ids: string[]) {
  try { localStorage.setItem(key, JSON.stringify(ids)); } catch { /* storage unavailable - state stays in-memory only */ }
}

// A refresh shouldn't dump you back on Weekly if you were looking at 4H -
// same reasoning as the watchlist/comparison persistence above.
const TIMEFRAME_LABELS = ["Weekly", "Daily", "4H", "1H", "15m"];
function loadPersistedTimeframe() {
  try {
    const saved = localStorage.getItem("rst_timeframe");
    return TIMEFRAME_LABELS.includes(saved) ? saved : "Weekly";
  } catch { return "Weekly"; }
}
function savePersistedTimeframe(timeframe) {
  try { localStorage.setItem("rst_timeframe", timeframe); } catch { /* storage unavailable - state stays in-memory only */ }
}

const state = {
  market: "All Markets",
  timeframe: loadPersistedTimeframe(),
  condition: "all",
  search: "",
  sort: "score",
  selected: "gold-w",
  comparison: loadPersistedIds("rst_comparison"),
  watchlist: loadPersistedIds("rst_watchlist"),
  scenario: "Base",
  catalyst: "Unchecked",
  overlayVisible: true,
  // "all" = normal market/timeframe-filtered browsing; "watchlist"/"compare"
  // switch the setup list to show only starred/comparison-selected setups,
  // driven by the matching sidebar nav item.
  view: "all"
};

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

function formatPrice(value, decimals) {
  return Number(value).toLocaleString("en-GB", { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

function directionClass(direction) { return direction.toLowerCase(); }
function scoreColor(score) { return score >= 80 ? "var(--green)" : score >= 70 ? "var(--amber)" : "var(--red)"; }

const tradingViewIntervals = { Weekly: "W", Daily: "D", "4H": "240", "1H": "60", "15m": "15" };

// --- Live data: the REAL Strategy/ engine via /api/signals/* (not the old
// v1 heuristic). The backend holds the Bybit / Twelve Data calls and keys
// server-side. Crypto (Bybit) has no daily quota, so it polls fast and
// scans all 5 timeframes in one call; Twelve Data's free tier caps at 800
// credits/key/day AND ~8 req/min/key, and the real engine can make up to 3
// sequential calls per (instrument, timeframe) evaluation (main candle +
// up to 2 HTF-context fetches, the latter cached), so FX/Metals polls one
// timeframe at a time on its own cadence via ?timeframe=, matching how fast
// that timeframe's candles actually close (e.g. 15m polls every 10 min, not
// every hour) instead of one blanket interval for everything. A localStorage
// guard also stops multiple open tabs from each polling independently and
// multiplying real request volume against that shared quota.
function shouldPoll(key: string, minIntervalMs: number): boolean {
  try {
    const last = Number(localStorage.getItem(key) || 0);
    if (Date.now() - last < minIntervalMs) return false;
    localStorage.setItem(key, String(Date.now()));
    return true;
  } catch {
    return true; // localStorage unavailable - fail open rather than never polling
  }
}

function markPolled(key: string) {
  try { localStorage.setItem(key, String(Date.now())); } catch { /* ignore */ }
}

// Grades come straight from Strategy/ConfluenceScore.cs's real 100-point
// model (A+ >=90, A >=85, B >=75, Watchlist >=65, else "No setup") - "qualified"
// here means the same thing that model means by it, not an arbitrary frontend rule.
const QUALIFIED_GRADES = new Set(["A+", "A", "B"]);

// PascalCase enum names ("TrendingBullish") -> readable display text and a
// coarser family used for the Market Condition filter dropdown (whose options
// are Trending/Ranging/Breakout/Reversal - one family covers both the
// bullish and bearish variant of a condition).
function formatEnumName(name: string): string {
  return name.replace(/([a-z0-9])([A-Z])/g, "$1 $2");
}
function conditionFamily(rawCondition: string): string {
  if (rawCondition.startsWith("Trending")) return "Trending";
  if (rawCondition.startsWith("Breakout")) return "Breakout";
  if (rawCondition === "ReversalDeveloping") return "Reversal";
  if (rawCondition === "Ranging") return "Ranging";
  return "Neutral";
}

function buildLevelRows(setup, signal) {
  const rows = [
    ["Entry", formatPrice(setup.entry, setup.decimals), "Real computed entry zone (preferred entry)"],
    ["Stop", formatPrice(setup.stop, setup.decimals), signal.invalidationConditions || "Computed stop"],
    ["TP1", formatPrice(setup.tp1, setup.decimals), "First partial-profit objective"],
    ["TP2 (R:R target)", formatPrice(setup.target, setup.decimals), "Reward-to-risk is quoted against this objective"],
    ["TP3", formatPrice(setup.tp3, setup.decimals), "Final extended objective"]
  ];
  for (const level of signal.keyLevels || []) {
    const value = level.upper === level.lower ? level.upper : (level.upper + level.lower) / 2;
    rows.push([
      formatEnumName(level.type),
      formatPrice(value, setup.decimals),
      `${level.timeframe} · ${level.mitigated ? "mitigated" : "unmitigated"}${level.invalidated ? " · invalidated" : ""} · ${level.reactionCount} reaction(s)`
    ]);
  }
  return rows;
}

// Only a small, known set of `reason` strings mean the engine actually ran
// on good data and honestly found nothing worth trading this scan (see
// SignalOrchestrator.cs's non-exception `return new OrchestratorResult(false, ...)`
// sites). Anything else - "Data unavailable or stale...", or an arbitrary
// caught-exception message - means the scan itself didn't really happen,
// so THAT is what should keep showing last-known data as stale, not an
// ordinary "no setup this time" outcome.
const GENUINE_NO_SETUP_PREFIXES = [
  "No setup model's precondition is met",
  "Setup model no longer applies on re-evaluation",
  "Hard economic-calendar veto active",
  "No valid entry/stop/target could be computed"
];
function isGenuineNoSetupReason(reason) {
  return typeof reason === "string" && GENUINE_NO_SETUP_PREFIXES.some(p => reason.startsWith(p));
}

// One entry in the /api/signals/* response array: { success, signal, reason,
// instrumentSymbol, timeframe }. Matched back onto the local setups array by
// symbol + timeframe label (both catalogs are built from the same source of
// truth - backend/Models/SetupDefinition.cs - so the strings line up exactly).
function applySignalResult(result) {
  const setup = setups.find(s => s.symbol === result.instrumentSymbol && s.timeframe === result.timeframe);
  if (!setup) return;

  if (result.success && result.signal) {
    const signal = result.signal;
    setup.hydrated = true;
    setup.stale = false;
    setup.live = true;
    setup.liveSource = signal.dataProvider;
    setup.liveError = null;
    setup.direction = signal.direction;
    setup.condition = formatEnumName(signal.condition);
    setup.conditionFamily = conditionFamily(signal.condition);
    setup.grade = signal.grade;
    setup.score = signal.setupQualityScore;
    setup.rr = Number(signal.rewardToRisk) || 0;
    setup.price = Number(signal.livePrice) || 0;
    setup.entry = Number(signal.preferredEntry) || 0;
    setup.stop = Number(signal.stop) || 0;
    setup.tp1 = Number(signal.tp1) || 0;
    setup.target = Number(signal.tp2) || 0; // R:R is quoted against TP2 - see EntryStopTarget.cs
    setup.tp3 = Number(signal.tp3) || 0;
    setup.confluences = (signal.confluenceFamilies || [])
      .filter(f => f.points > 0)
      .sort((a, b) => b.points - a.points)
      .map(f => `${f.family} (${f.points}/${f.maxPoints}): ${f.basis}`);
    setup.reasoning = signal.reasoningSummary || "No confirmed requirements to summarise yet.";
    setup.levels = buildLevelRows(setup, signal);
    setup.newsState = signal.newsState;
    setup.economicCalendarState = signal.economicCalendarState;
  } else if (setup.hydrated && !isGenuineNoSetupReason(result.reason)) {
    // A real fetch/compute failure (bad data, an exception) on a setup that
    // previously had a genuine result - keep the last-known values rather
    // than wiping them to zero, but say plainly that this scan didn't
    // actually refresh them.
    setup.liveError = result.reason || "Scan failed";
    setup.stale = true;
    setup.live = false;
  } else {
    // The engine DID run this scan against good data and honestly found
    // nothing worth trading - that's a fresh, live result in its own right
    // (not a cache miss), so it fully replaces whatever was there before
    // instead of being mislabeled "last known"/stale just because a prior
    // scan happened to find something.
    setup.hydrated = true;
    setup.stale = false;
    setup.live = true;
    setup.liveSource = null;
    setup.liveError = null;
    setup.direction = "Neutral";
    setup.condition = "No qualifying setup";
    setup.conditionFamily = "Neutral";
    setup.grade = "No setup";
    setup.score = 0;
    setup.rr = 0;
    setup.reasoning = result.reason || "No qualifying setup was found on this scan.";
    setup.levels = [["—", "—", "No qualifying setup on this scan"]];
    setup.confluences = [];
  }
  setup.cachedAtMs = Date.now();
}

// Persists the last real (or last-known-stale) values per setup id, restored
// on load BEFORE any network call resolves - so a page reload (including
// Angular's dev-server hot-reload) shows the last genuine analysis instead
// of resetting every card to zero/"Awaiting live data" for the several
// seconds it takes the FX poll (paced, sequential) to catch back up.
const SETUP_CACHE_KEY = "rst_setup_cache";
const CACHED_FIELDS = [
  "hydrated", "live", "liveSource", "liveError", "direction", "condition", "conditionFamily",
  "grade", "score", "rr", "price", "entry", "stop", "tp1", "target", "tp3",
  "confluences", "reasoning", "levels", "newsState", "economicCalendarState", "cachedAtMs"
];

function persistSetupCache() {
  try {
    const cache: Record<string, unknown> = {};
    for (const setup of setups) {
      if (!setup.hydrated) continue;
      const entry: Record<string, unknown> = {};
      for (const field of CACHED_FIELDS) entry[field] = setup[field];
      cache[setup.id] = entry;
    }
    localStorage.setItem(SETUP_CACHE_KEY, JSON.stringify(cache));
  } catch { /* storage unavailable or full - fall back to live-only state */ }
}

// How often THIS setup would normally get a fresh read, absent a reload -
// the yardstick for whether a restored cache entry still counts as current.
function expectedPollIntervalMs(timeframeLabel: string): number {
  return FX_POLL_MS_BY_TIMEFRAME[timeframeLabel] ?? CRYPTO_POLL_MS;
}

function restoreSetupCache() {
  try {
    const cache = JSON.parse(localStorage.getItem(SETUP_CACHE_KEY) || "{}");
    const now = Date.now();
    for (const setup of setups) {
      const cached = cache[setup.id];
      if (!cached) continue;
      Object.assign(setup, cached);
      // A reload doesn't make real data stale by itself - only flag it as
      // "last known" if it's actually older than this setup would normally
      // wait between polls. Otherwise a page refresh moments after a good
      // fetch would wrongly relabel perfectly current data as stale and
      // reset "No qualifying setup" placeholders that were never re-checked.
      const age = now - (cached.cachedAtMs || 0);
      if (age > expectedPollIntervalMs(setup.timeframe)) {
        setup.live = false;
        setup.stale = true;
      }
    }
  } catch { /* corrupt/absent cache - setups keep their honest zeroed defaults */ }
}

function timeAgo(ms: number): string {
  const minutes = Math.max(1, Math.round((Date.now() - ms) / 60000));
  if (minutes < 60) return `${minutes}m ago`;
  return `${Math.round(minutes / 60)}h ago`;
}

async function fetchSignals(path: string) {
  try {
    const response = await fetch(`${API_BASE}${path}`);
    if (!response.ok) throw new Error(`Backend API ${response.status}`);
    const results = await response.json();
    for (const result of results) applySignalResult(result);
    persistSetupCache();
  } catch (err) {
    // Batch fetch itself failed (network down, backend restarting) - leave
    // every setup exactly as it was rather than wiping them; the next poll
    // tries again on its own schedule.
    console.error(`Could not reach ${path}:`, err);
  }
  refresh();
}

const CRYPTO_POLL_MS = 5 * 60 * 1000; // Bybit: keyless/no quota, all 5 timeframes in one call

// Twelve Data: ~8 req/min/key and 800 credits/key/day. Each evaluation costs
// 1 call for its own candles plus up to 2 HTF-context calls (usually cache-hit
// after the first poll in a session - see SignalOrchestrator's context cache).
// Cadence is set relative to how often each timeframe's candle actually closes
// - matches the explicit requirement that the 15-minute tier polls every
// ~10 minutes rather than sharing one hourly interval with Weekly/Daily.
const FX_TIMEFRAMES = ["Weekly", "Daily", "4H", "1H", "15m"];
const FX_POLL_MS_BY_TIMEFRAME: Record<string, number> = {
  "15m": 10 * 60 * 1000,
  "1H": 20 * 60 * 1000,
  "4H": 45 * 60 * 1000,
  Daily: 2 * 60 * 60 * 1000,
  Weekly: 6 * 60 * 60 * 1000
};

// The localStorage guard only throttles the recurring poll (so multiple open
// tabs don't each re-fetch on every tick) - it never blocks a fresh page
// load, which always needs real data immediately rather than sitting on
// hardcoded defaults until some earlier tab's timer window happens to lapse.
function hydrateCryptoPoll() {
  if (!shouldPoll("rst_last_crypto_fetch", CRYPTO_POLL_MS - 5000)) return;
  fetchSignals("/api/signals/crypto");
}

function hydrateFxPoll(timeframeLabel: string) {
  const intervalMs = FX_POLL_MS_BY_TIMEFRAME[timeframeLabel];
  if (!shouldPoll(`rst_last_fx_fetch_${timeframeLabel}`, intervalMs - 5000)) return;
  fetchSignals(`/api/signals/fx?timeframe=${encodeURIComponent(timeframeLabel)}`);
}

// Statically deployed (e.g. GitHub Pages via a scheduled GitHub Actions
// workflow - see .github/workflows/) means there is no live backend to poll
// at all: a scheduled job runs the real engine once per cadence and commits
// a signals.json snapshot alongside this build. Detected by hostname rather
// than an Angular build config, since this app has no environment.ts split
// yet - a reasonable shortcut for now, worth doing properly later.
const IS_STATIC_DEPLOYMENT = typeof location !== "undefined" &&
  location.hostname !== "localhost" && location.hostname !== "127.0.0.1";

let lastSnapshotGeneratedAtMs = 0;

async function fetchStaticSnapshot() {
  try {
    // Cache-bust: browsers/CDNs must not serve a stale copy of this file
    // between GitHub Actions runs, or the dashboard would look "live" while
    // actually frozen on old data.
    const response = await fetch(`signals.json?_=${Date.now()}`, { cache: "no-store" });
    if (!response.ok) throw new Error(`Snapshot fetch ${response.status}`);
    const snapshot = await response.json();
    for (const result of snapshot.results || []) applySignalResult(result);
    persistSetupCache();
    lastSnapshotGeneratedAtMs = snapshot.generatedAtUtc ? new Date(snapshot.generatedAtUtc).getTime() : Date.now();
  } catch (err) {
    // Leaves everything as last-known (see applySignalResult/restoreSetupCache) -
    // never wipes the dashboard just because one static-file fetch failed.
    console.error("Could not reach signals.json snapshot:", err);
  }
  refresh();
}

// --- Signal tracker: a permanent ledger of every real qualification (grade
// B+) and its tracked outcome (see backend/Services/SignalLogService.cs),
// gated behind a passphrase. Explicitly NOT real security - the underlying
// signal-log.json is still directly fetchable by anyone who knows/guesses
// its URL, and this hash is visible in the shipped bundle. It only deters
// casual browsing, which is the level of protection asked for.
//
// To change the passphrase: open the browser console anywhere on this site
// and run
//   crypto.subtle.digest("SHA-256", new TextEncoder().encode("your new passphrase"))
//     .then(b => console.log([...new Uint8Array(b)].map(x => x.toString(16).padStart(2,"0")).join("")))
// then paste the printed hex string in as TRACKER_PASSPHRASE_HASH below.
// Placeholder passphrase for now: "realtouch2026" - change this before relying on it.
const TRACKER_PASSPHRASE_HASH = "5b470109d29f223f1defab078f99bc2769eb27071fa1c6e5cab2075dfa417976";
const TRACKER_UNLOCK_KEY = "rst_tracker_unlocked";

async function sha256Hex(text) {
  const bytes = new TextEncoder().encode(text);
  const hashBuffer = await crypto.subtle.digest("SHA-256", bytes);
  return [...new Uint8Array(hashBuffer)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function isTrackerUnlocked() {
  try { return sessionStorage.getItem(TRACKER_UNLOCK_KEY) === "1"; } catch { return false; }
}

async function tryUnlockTracker(passphrase) {
  const hash = await sha256Hex(passphrase || "");
  if (hash !== TRACKER_PASSPHRASE_HASH) return false;
  try { sessionStorage.setItem(TRACKER_UNLOCK_KEY, "1"); } catch { /* falls back to re-prompting next time */ }
  return true;
}

function trackerStatusLabel(status) {
  switch (status) {
    case "Open": return "Open";
    case "Tp1Hit": return "TP1 hit";
    case "Tp2Hit": return "TP2 hit";
    case "Tp3Hit": return "TP3 hit (win)";
    case "StoppedOut": return "Stopped out (loss)";
    case "Expired": return "Expired (flat)";
    default: return status;
  }
}

function trackerStatusClass(status) {
  if (status === "Tp3Hit") return "positive";
  if (status === "StoppedOut") return "negative";
  return "";
}

async function fetchTrackerEntries() {
  const path = IS_STATIC_DEPLOYMENT ? `signal-log.json?_=${Date.now()}` : `${API_BASE}/api/signal-log`;
  const response = await fetch(path, IS_STATIC_DEPLOYMENT ? { cache: "no-store" } : undefined);
  if (!response.ok) throw new Error(`Tracker fetch ${response.status}`);
  return response.json();
}

// Client-side state for the currently-open tracker: the last fetched
// entries (re-filtered locally as the toolbar changes, not re-fetched), and
// which row ids are checked for a bulk delete.
let trackerEntries = [];
let trackerSelectedIds = new Set();

function filteredTrackerEntries() {
  const status = $("#trackerStatusFilter")?.value || "all";
  const query = ($("#trackerSearch")?.value || "").trim().toLowerCase();
  return trackerEntries.filter(e => {
    const statusOk = status === "all"
      || (status === "open" ? (e.status === "Open" || e.status === "Tp1Hit" || e.status === "Tp2Hit") : e.status === status);
    const queryOk = !query || `${e.symbol} ${e.timeframe}`.toLowerCase().includes(query);
    return statusOk && queryOk;
  });
}

async function renderSignalTracker() {
  const body = $("#trackerBody");
  if (!body) return;
  body.innerHTML = `<tr><td colspan="12" class="tracker-empty">Loading…</td></tr>`;
  trackerSelectedIds.clear();

  // Delete/reset need a live backend to act on - the static deployment is a
  // read-only JSON snapshot with nothing listening on the other end.
  $("#deleteSelectedRows").hidden = IS_STATIC_DEPLOYMENT;
  $("#resetTracker").hidden = IS_STATIC_DEPLOYMENT;
  $("#trackerReadonlyNote").hidden = !IS_STATIC_DEPLOYMENT;

  try {
    trackerEntries = await fetchTrackerEntries();
    renderTrackerRows();
    renderTrackerStats();
  } catch (err) {
    console.error("Could not reach the signal tracker log:", err);
    trackerEntries = [];
    body.innerHTML = `<tr><td colspan="12" class="tracker-empty">Could not load the tracker log.</td></tr>`;
    renderTrackerStats();
  }
}

// Reporting only - real win rate / average realized R computed from your
// own resolved trades. This never feeds back into the scoring or
// qualification engine; it's purely informational until there's a real,
// meaningful sample size to justify anything more automated.
const RESOLVED_STATUSES = ["Tp3Hit", "StoppedOut", "Expired"];

function computeTrackerBreakdown(entries, keyFn) {
  const groups = new Map();
  for (const e of entries) {
    const key = keyFn(e);
    if (!groups.has(key)) groups.set(key, { key, resolved: 0, wins: 0, losses: 0, flat: 0, sumR: 0 });
    const g = groups.get(key);
    g.resolved++;
    if (e.status === "Tp3Hit") g.wins++;
    else if (e.status === "StoppedOut") g.losses++;
    else g.flat++;
    g.sumR += e.realizedR || 0;
  }
  return [...groups.values()]
    .map(g => ({ ...g, winRate: g.resolved ? (g.wins / g.resolved) * 100 : 0, avgR: g.resolved ? g.sumR / g.resolved : 0 }))
    .sort((a, b) => b.resolved - a.resolved);
}

function renderStatsTable(title, rows) {
  if (!rows.length) return "";
  return `
    <div class="tracker-stats-section">
      <h3>${title}</h3>
      <table class="tracker-stats-table">
        <thead><tr><th>${title}</th><th>Resolved</th><th>Wins</th><th>Losses</th><th>Flat</th><th>Win rate</th><th>Avg R</th></tr></thead>
        <tbody>${rows.map(r => `
          <tr>
            <td>${r.key}</td>
            <td>${r.resolved}</td>
            <td class="positive">${r.wins}</td>
            <td class="negative">${r.losses}</td>
            <td>${r.flat}</td>
            <td>${r.winRate.toFixed(0)}%</td>
            <td class="${r.avgR > 0 ? "positive" : r.avgR < 0 ? "negative" : ""}">${r.avgR.toFixed(2)}R</td>
          </tr>`).join("")}</tbody>
      </table>
    </div>`;
}

function renderTrackerStats() {
  const container = $("#trackerStatsBody");
  if (!container) return;
  const resolved = trackerEntries.filter(e => RESOLVED_STATUSES.includes(e.status));
  const openCount = trackerEntries.length - resolved.length;

  if (!resolved.length) {
    container.innerHTML = `<p class="markup-tip" style="padding:18px;">No resolved trades yet${openCount ? ` (${openCount} still open)` : ""} - performance stats need at least a few closed setups to show anything meaningful.</p>`;
    return;
  }

  const wins = resolved.filter(e => e.status === "Tp3Hit").length;
  const losses = resolved.filter(e => e.status === "StoppedOut").length;
  const flat = resolved.filter(e => e.status === "Expired").length;
  const winRate = (wins / resolved.length) * 100;
  const avgR = resolved.reduce((sum, e) => sum + (e.realizedR || 0), 0) / resolved.length;

  container.innerHTML = `
    <div class="tracker-stats-grid">
      <div class="tracker-stat-tile"><span>Resolved trades</span><strong>${resolved.length}${openCount ? ` <small style="font-size:9px;color:var(--muted-2)">(+${openCount} open)</small>` : ""}</strong></div>
      <div class="tracker-stat-tile"><span>Win rate</span><strong>${winRate.toFixed(0)}%</strong></div>
      <div class="tracker-stat-tile"><span>Avg realized R</span><strong class="${avgR > 0 ? "positive" : avgR < 0 ? "negative" : ""}">${avgR.toFixed(2)}R</strong></div>
      <div class="tracker-stat-tile"><span>W / L / Flat</span><strong>${wins} / ${losses} / ${flat}</strong></div>
    </div>
    ${renderStatsTable("Setup model", computeTrackerBreakdown(resolved, e => e.setupModel))}
    ${renderStatsTable("Grade", computeTrackerBreakdown(resolved, e => e.grade))}
    ${renderStatsTable("Timeframe", computeTrackerBreakdown(resolved, e => e.timeframe))}
    ${renderStatsTable("Symbol", computeTrackerBreakdown(resolved, e => e.symbol))}`;
}

function renderTrackerRows() {
  const body = $("#trackerBody");
  if (!body) return;
  const rows = filteredTrackerEntries();

  if (!rows.length) {
    body.innerHTML = `<tr><td colspan="12" class="tracker-empty">${trackerEntries.length ? "No rows match this filter." : "No qualified signals logged yet."}</td></tr>`;
  } else {
    body.innerHTML = rows.map(e => `
      <tr>
        <td>${IS_STATIC_DEPLOYMENT ? "" : `<input type="checkbox" class="tracker-row-check" data-id="${e.id}" ${trackerSelectedIds.has(e.id) ? "checked" : ""} aria-label="Select row" />`}</td>
        <td>${e.symbol}</td>
        <td>${e.timeframe}</td>
        <td><span class="direction ${e.direction.toLowerCase()}">${e.direction.toUpperCase()}</span></td>
        <td>${e.grade} (${e.score})</td>
        <td>${formatPrice(e.entry, 5)}</td>
        <td>${formatPrice(e.stop, 5)}</td>
        <td>${e.rewardToRisk.toFixed(1)}R</td>
        <td>${new Date(e.qualifiedAtUtc).toLocaleString()}</td>
        <td>${RESOLVED_STATUSES.includes(e.status) && e.closedAtUtc ? new Date(e.closedAtUtc).toLocaleString() : "—"}</td>
        <td class="${trackerStatusClass(e.status)}">${trackerStatusLabel(e.status)}</td>
        <td class="${e.realizedR == null ? "" : e.realizedR > 0 ? "positive" : e.realizedR < 0 ? "negative" : ""}">${e.realizedR == null ? "—" : `${e.realizedR.toFixed(2)}R`}</td>
        <td>${IS_STATIC_DEPLOYMENT ? "" : `<button type="button" class="icon-button small" data-delete-row="${e.id}" title="Delete this row" aria-label="Delete this row">×</button>`}</td>
      </tr>`).join("");
  }
  updateTrackerSelectionUi();
}

function updateTrackerSelectionUi() {
  const count = trackerSelectedIds.size;
  const countEl = $("#trackerSelectedCount");
  if (countEl) { countEl.hidden = count === 0; countEl.textContent = `${count} selected`; }
  const deleteBtn = $("#deleteSelectedRows");
  if (deleteBtn) deleteBtn.disabled = count === 0;
  const visibleIds = filteredTrackerEntries().map(e => e.id);
  const selectAll = $("#trackerSelectAll");
  if (selectAll) selectAll.checked = visibleIds.length > 0 && visibleIds.every(id => trackerSelectedIds.has(id));
}

// Generic confirmation dialog reused for single-row delete, bulk delete, and
// reset-all - every one of these is a permanent, real-money-relevant action
// with no undo, so none of them fire without this gate.
let pendingConfirmAction = null;

function openConfirm(message, buttonLabel, onProceed) {
  $("#confirmMessage").textContent = message;
  $("#proceedConfirm").textContent = buttonLabel;
  pendingConfirmAction = onProceed;
  $("#confirmModal").hidden = false;
  document.body.style.overflow = "hidden";
}

function closeConfirmModal() {
  $("#confirmModal").hidden = true;
  document.body.style.overflow = "";
  pendingConfirmAction = null;
}

async function deleteTrackerRow(id) {
  const entry = trackerEntries.find(e => e.id === id);
  const label = entry ? `${entry.symbol} · ${entry.timeframe} (qualified ${new Date(entry.qualifiedAtUtc).toLocaleString()})` : "this row";
  openConfirm(`Permanently delete the tracker row for ${label}? This cannot be undone.`, "Delete", async () => {
    try {
      const response = await fetch(`${API_BASE}/api/signal-log/${id}`, { method: "DELETE" });
      if (!response.ok) throw new Error(`Delete failed: ${response.status}`);
      trackerSelectedIds.delete(id);
      await renderSignalTracker();
      showToast("Row deleted");
    } catch (err) {
      console.error("Could not delete tracker row:", err);
      showToast("Could not delete - is the backend running?");
    }
  });
}

function deleteSelectedTrackerRows() {
  const ids = [...trackerSelectedIds];
  if (!ids.length) return;
  openConfirm(`Permanently delete ${ids.length} selected row${ids.length === 1 ? "" : "s"}? This cannot be undone.`, "Delete", async () => {
    let failures = 0;
    for (const id of ids) {
      try {
        const response = await fetch(`${API_BASE}/api/signal-log/${id}`, { method: "DELETE" });
        if (!response.ok) failures++;
      } catch { failures++; }
    }
    await renderSignalTracker();
    showToast(failures ? `Deleted ${ids.length - failures}, ${failures} failed` : `${ids.length} row${ids.length === 1 ? "" : "s"} deleted`);
  });
}

function resetTrackerLedger() {
  openConfirm("Permanently delete the ENTIRE tracker ledger - every logged setup and its outcome? This cannot be undone.", "Reset all", async () => {
    try {
      const response = await fetch(`${API_BASE}/api/signal-log`, { method: "DELETE" });
      if (!response.ok) throw new Error(`Reset failed: ${response.status}`);
      await renderSignalTracker();
      showToast("Tracker ledger reset");
    } catch (err) {
      console.error("Could not reset tracker ledger:", err);
      showToast("Could not reset - is the backend running?");
    }
  });
}

function openSignalTracker() {
  if (isTrackerUnlocked()) {
    showTrackerModal();
  } else {
    openPassphraseModal();
  }
}

function showTrackerModal() {
  $("#trackerModal").hidden = false;
  document.body.style.overflow = "hidden";
  renderSignalTracker();
}

function closeSignalTracker() {
  $("#trackerModal").hidden = true;
  document.body.style.overflow = "";
}

function openPassphraseModal() {
  $("#passphraseError").hidden = true;
  $("#passphraseInput").classList.remove("input-invalid");
  $("#passphraseInput").value = "";
  $("#passphraseModal").hidden = false;
  document.body.style.overflow = "hidden";
  $("#passphraseInput").focus();
}

function closePassphraseModal() {
  $("#passphraseModal").hidden = true;
  document.body.style.overflow = "";
}

async function submitPassphrase() {
  const input = $("#passphraseInput");
  const ok = await tryUnlockTracker(input.value);
  if (!ok) {
    input.classList.add("input-invalid");
    $("#passphraseError").hidden = false;
    input.focus();
    input.select();
    return;
  }
  closePassphraseModal();
  showTrackerModal();
}

const catalystProfiles = {
  "Crypto Futures": ["USD rates & liquidity", "Risk sentiment", "ETF / regulation headlines", "Exchange-specific flows"],
  FX: ["Central-bank policy", "Inflation & employment", "Yield differentials", "Affected base/quote currencies"],
  Metals: ["USD & real yields", "Fed expectations", "Inflation data", "Geopolitical risk"],
  Energy: ["Inventories & production", "OPEC+ headlines", "USD direction", "Geopolitical supply risk"]
};

const sessions = [
  { name: "Sydney", region: "Pacific", zone: "Australia/Sydney", open: 8, close: 17, relevance: "AUD, NZD and early crypto flow" },
  { name: "Tokyo", region: "Asia", zone: "Asia/Tokyo", open: 8, close: 17, relevance: "JPY, Asia risk and crypto" },
  { name: "London", region: "Europe", zone: "Europe/London", open: 8, close: 17, relevance: "GBP, EUR, FX and metals" },
  { name: "New York", region: "Americas", zone: "America/New_York", open: 8, close: 17, relevance: "USD, metals, energy and crypto" }
];

function tradingViewSymbol(setup) { return setup.tvSymbol || "OANDA:EURUSD"; }

function mountTradingViewWidget(target, source, config) {
  if (!target) return;
  target.innerHTML = '<div class="tradingview-widget-container"><div class="tradingview-widget-container__widget"><div class="widget-loading">Connecting to TradingView…</div></div></div>';
  const container = target.firstElementChild;
  const script = document.createElement("script");
  script.type = "text/javascript";
  script.src = source;
  script.async = true;
  script.textContent = JSON.stringify(config);
  script.onerror = () => { target.innerHTML = '<div class="widget-loading">Live feed could not load. Check the network connection and try again.</div>'; };
  container.appendChild(script);
}

function overlayLevels(setup) {
  const values = [setup.target, setup.entry, setup.stop];
  const high = Math.max(...values);
  const low = Math.min(...values);
  const range = high - low || 1;
  const y = value => 8 + ((high - value) / range) * 84;
  return [
    { label: "TARGET / LIQUIDITY", value: setup.target, colour: "#52d49c", y: y(setup.target) },
    { label: "ENTRY / CONFIRMATION", value: setup.entry, colour: "#79aaff", y: y(setup.entry) },
    { label: "STOP / INVALIDATION", value: setup.stop, colour: "#ff6f78", y: y(setup.stop) }
  ];
}

function confluenceType(text) {
  if (/order block/i.test(text)) return "ORDER BLOCK";
  if (/fair value|imbalance/i.test(text)) return "FVG / IMBALANCE";
  if (/liquidity|swept|sweep/i.test(text)) return "LIQUIDITY";
  if (/structure|change of character|higher-high|lower-high/i.test(text)) return "STRUCTURE";
  if (/willis/i.test(text)) return "WILLIS ZONE";
  if (/volume|momentum|flow/i.test(text)) return "FLOW / MOMENTUM";
  if (/reward|risk|\br\b/i.test(text)) return "RISK QUALITY";
  return "CONFLUENCE";
}

function renderLiveChart(setup) {
  $("#liveDataStatus").textContent = `${setup.symbol} · ${setup.timeframe} · LIVE`;
  mountTradingViewWidget($("#liveChart"), "https://s3.tradingview.com/external-embedding/embed-widget-advanced-chart.js", {
    autosize: true,
    symbol: tradingViewSymbol(setup),
    interval: tradingViewIntervals[setup.timeframe] || "60",
    timezone: "Etc/UTC",
    theme: "dark",
    backgroundColor: "rgba(9,19,15,1)",
    gridColor: "rgba(108,142,128,0.10)",
    style: "1",
    locale: "en",
    allow_symbol_change: true,
    save_image: false,
    calendar: false,
    hide_side_toolbar: false,
    withdateranges: true,
    studies: ["STD;EMA"],
    support_host: "https://www.tradingview.com"
  });
}

let mountedNewsSymbol = "";
let calendarMounted = false;
function renderLiveIntelligence(setup, force = false) {
  const symbol = tradingViewSymbol(setup);
  $("#newsTitle").textContent = `${setup.symbol} market headlines`;
  $("#catalystSummary").innerHTML = catalystProfiles[setup.group].map((item, index) => `<span class="catalyst-chip"><strong>${index + 1}</strong> ${item}</span>`).join("");
  if (force || mountedNewsSymbol !== symbol) {
    mountedNewsSymbol = symbol;
    mountTradingViewWidget($("#newsWidget"), "https://s3.tradingview.com/external-embedding/embed-widget-timeline.js", {
      feedMode: "symbol",
      symbol,
      isTransparent: true,
      displayMode: "regular",
      width: "100%",
      height: "100%",
      colorTheme: "dark",
      locale: "en"
    });
  }
  if (!calendarMounted || force) {
    calendarMounted = true;
    mountTradingViewWidget($("#calendarWidget"), "https://s3.tradingview.com/external-embedding/embed-widget-events.js", {
      colorTheme: "dark",
      isTransparent: true,
      width: "100%",
      height: "100%",
      locale: "en",
      importanceFilter: "-1,0,1",
      countryFilter: "us,gb,eu,jp,au,nz,ca,ch,cn"
    });
  }
}

function renderMarketNav() {
  const groups = Object.keys(groupMeta);
  // setups has one row per instrument PER TIMEFRAME, so counting by group
  // alone over-counts every market by 5x. Scope to the active timeframe so
  // the badge matches the actual number of distinct markets in that group
  // (and lines up with what the results list shows once you click it).
  $("#marketNav").innerHTML = groups.map(group => {
    const count = setups.filter(s => s.timeframe === state.timeframe && (group === "All Markets" || s.group === group)).length;
    return `<button class="market-filter ${state.market === group ? "active" : ""}" style="--group-color:${groupMeta[group].color}" data-market="${group}" type="button"><i></i><span>${group}</span><b>${count}</b></button>`;
  }).join("");
}

function renderTimeframes() {
  const frames = ["Weekly", "Daily", "4H", "1H", "15m"];
  $("#timeframeFilters").innerHTML = frames.map(frame => `<button class="filter-pill ${state.timeframe === frame ? "active" : ""}" data-timeframe="${frame}" type="button">${frame}</button>`).join("");
}

function filteredSetups() {
  const query = state.search.trim().toLowerCase();
  let list = setups;
  if (state.view === "watchlist") {
    list = list.filter(s => state.watchlist.includes(s.id));
  } else if (state.view === "compare") {
    list = list.filter(s => state.comparison.includes(s.id));
  } else {
    list = list
      .filter(s => state.market === "All Markets" || s.group === state.market)
      .filter(s => s.timeframe === state.timeframe)
      .filter(s => state.condition === "all" || s.conditionFamily === state.condition);
  }
  return list
    .filter(s => !query || `${s.symbol} ${s.name} ${s.group}`.toLowerCase().includes(query))
    .sort((a, b) => state.sort === "rr" ? b.rr - a.rr : state.sort === "recent" ? a.updated - b.updated : b.score - a.score);
}

// Scoped like the sidebar market-nav badges: current timeframe + market +
// condition + search, everything except the watchlist/compare view. Without
// the timeframe filter this mixes a market's Weekly bias with its 15m bias
// in the same Bullish/Bearish bucket, and "Markets scanned" over-counts by
// 5x (one row per instrument per timeframe) instead of counting markets.
function groupFilteredSetups() {
  const query = state.search.trim().toLowerCase();
  return setups
    .filter(s => s.timeframe === state.timeframe)
    .filter(s => state.market === "All Markets" || s.group === state.market)
    .filter(s => state.condition === "all" || s.conditionFamily === state.condition)
    .filter(s => !query || `${s.symbol} ${s.name} ${s.group}`.toLowerCase().includes(query));
}

// The subtitle under each symbol: honestly distinguishes "fresh live read"
// from "last known value, now stale" from "never scanned" from "just failed
// with no prior data" - never presents cached/stale data as if it were live.
function sourceLabel(s) {
  if (s.comingSoon) return `${s.group} · COMING SOON`;
  if (s.stale) return `${s.group} · LAST KNOWN (${timeAgo(s.cachedAtMs)})`;
  if (s.live) return s.liveSource ? `${s.group} · LIVE (${s.liveSource})` : `${s.group} · LIVE (scanned)`;
  if (s.hydrated && s.liveError) return `${s.group} · FAILED: ${s.liveError}`;
  return `${s.group} · Awaiting live data`;
}

function renderSetupList() {
  const results = filteredSetups();
  $("#resultCount").textContent = `${results.length} result${results.length === 1 ? "" : "s"}`;
  $("#setupList").innerHTML = results.map(s => `
    <button class="setup-card ${state.selected === s.id ? "active" : ""} ${s.comingSoon ? "coming-soon" : ""} ${s.stale ? "stale-data" : ""}" data-setup="${s.id}" type="button">
      <div class="setup-card-top">
        <div class="asset-symbol"><span class="asset-icon" style="--group-color:${groupMeta[s.group].color}">${s.icon}</span><span><strong>${s.symbol}</strong><small title="${s.liveError || ""}" ${s.comingSoon || s.stale ? 'class="coming-soon-text"' : ""}>${sourceLabel(s)}</small></span></div>
        <span class="score-ring" style="--score:${s.score};--score-color:${scoreColor(s.score)}"><b>${s.comingSoon ? "—" : s.score}</b></span>
      </div>
      <div class="setup-card-middle"><span class="direction ${directionClass(s.direction)}">${s.comingSoon ? "NOT LIVE" : s.direction.toUpperCase()}</span><span class="condition">${s.condition}${s.grade ? ` · ${s.grade}` : ""}</span><span class="timeframe">${s.timeframe}</span></div>
      <div class="setup-card-bottom">
        <span class="mini-stat"><span>Entry</span><strong>${s.comingSoon ? "—" : formatPrice(s.entry, s.decimals)}</strong></span>
        <span class="mini-stat"><span>R:R</span><strong>${s.comingSoon ? "—" : `${s.rr.toFixed(1)}R`}</strong></span>
        <span class="mini-stat"><span>Confluences</span><strong>${s.comingSoon ? "—" : s.confluences.length}</strong></span>
        ${s.comingSoon ? "" : `<span class="card-actions">
          <span class="card-action ${state.watchlist.includes(s.id) ? "selected" : ""}" data-action="watch" title="Add to watchlist">${state.watchlist.includes(s.id) ? "★" : "☆"}</span>
          <span class="card-action ${state.comparison.includes(s.id) ? "selected" : ""}" data-action="compare" title="Compare setup">⇄</span>
        </span>`}
      </div>
    </button>`).join("");
  const emptyState = $("#emptyState");
  if (emptyState) {
    const [title, message] = state.view === "watchlist"
      ? ["Your watchlist is empty", "Use the ☆ action on any setup card to add it here."]
      : state.view === "compare"
      ? ["Select at least two setups to compare", "Use the ⇄ action on any setup card, then reopen this tab."]
      : ["No matching setups", "Adjust your market, timeframe, or condition filters."];
    const heading = emptyState.querySelector("h3");
    const body = emptyState.querySelector("p");
    if (heading) heading.textContent = title;
    if (body) body.textContent = message;
    emptyState.hidden = results.length > 0;
  }
  $("#setupList").hidden = results.length === 0;
}

function zoneParts(date, zone) {
  const parts = new Intl.DateTimeFormat("en-GB", {
    timeZone: zone, weekday: "short", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23"
  }).formatToParts(date);
  return Object.fromEntries(parts.filter(p => p.type !== "literal").map(p => [p.type, p.value]));
}

function zoneOffsetHours(zone, date = new Date()) {
  const parts = Object.fromEntries(new Intl.DateTimeFormat("en-CA", {
    timeZone: zone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23"
  }).formatToParts(date).filter(p => p.type !== "literal").map(p => [p.type, p.value]));
  const representedUtc = Date.UTC(+parts.year, +parts.month - 1, +parts.day, +parts.hour, +parts.minute, +parts.second);
  return Math.round(((representedUtc - date.getTime()) / 3600000) * 2) / 2;
}

function localHourToUtc(zone, localHour, date = new Date()) {
  return ((localHour - zoneOffsetHours(zone, date)) % 24 + 24) % 24;
}

function addHours(hour, amount) { return (hour + amount + 24) % 24; }

function setHeatRange(levels, start, end, value) {
  let cursor = Math.floor(start);
  const finish = Math.ceil(end);
  for (let guard = 0; guard < 24; guard++) {
    const hour = ((cursor % 24) + 24) % 24;
    levels[hour] = Math.max(levels[hour], value);
    cursor += 1;
    if (((cursor - finish) % 24 + 24) % 24 === 0) break;
  }
}

function volatilityModel(setup, now = new Date()) {
  const londonOpen = localHourToUtc("Europe/London", 8, now);
  const londonClose = localHourToUtc("Europe/London", 17, now);
  const newYorkOpen = localHourToUtc("America/New_York", 8, now);
  const tokyoOpen = localHourToUtc("Asia/Tokyo", 8, now);
  const overlapEnd = londonClose;
  const levels = Array(24).fill(setup.group.includes("Crypto") ? 2 : 1);
  const windows = [];

  if (setup.group === "FX") {
    setHeatRange(levels, tokyoOpen, addHours(tokyoOpen, 3), setup.symbol.includes("JPY") ? 3 : 2);
    setHeatRange(levels, londonOpen, addHours(londonOpen, 3), 3);
    setHeatRange(levels, newYorkOpen, overlapEnd, 4);
    windows.push([newYorkOpen, overlapEnd, "London–New York overlap", "Peak liquidity for USD, GBP and EUR pairs.", true]);
    windows.push([londonOpen, addHours(londonOpen, 3), "London opening drive", "Strong price discovery for European currencies.", false]);
    if (setup.symbol.includes("JPY")) windows.push([tokyoOpen, addHours(tokyoOpen, 3), "Tokyo opening drive", "Additional JPY liquidity and regional participation.", false]);
  } else if (setup.group === "Metals") {
    setHeatRange(levels, londonOpen, addHours(londonOpen, 3), 3);
    setHeatRange(levels, addHours(newYorkOpen, -1), addHours(newYorkOpen, 5), 4);
    windows.push([addHours(newYorkOpen, -1), addHours(newYorkOpen, 4), "US data & New York flow", "Often the strongest gold and silver repricing window.", true]);
    windows.push([londonOpen, addHours(londonOpen, 3), "London metals window", "European liquidity and positioning become active.", false]);
  } else if (setup.group === "Energy") {
    setHeatRange(levels, londonOpen, newYorkOpen, 2);
    setHeatRange(levels, newYorkOpen, addHours(newYorkOpen, 6), 4);
    windows.push([newYorkOpen, addHours(newYorkOpen, 6), "New York energy window", "Highest typical WTI/Brent participation; check inventory events.", true]);
    windows.push([newYorkOpen, overlapEnd, "London–New York overlap", "Cross-region liquidity can accelerate directional moves.", false]);
  } else {
    setHeatRange(levels, londonOpen, addHours(londonOpen, 3), 3);
    setHeatRange(levels, newYorkOpen, overlapEnd, 4);
    setHeatRange(levels, addHours(newYorkOpen, 0), addHours(newYorkOpen, 5), 3);
    windows.push([newYorkOpen, overlapEnd, "Europe–US overlap", "Typically the deepest cross-region crypto liquidity.", true]);
    windows.push([londonOpen, addHours(londonOpen, 3), "European activation", "European participation often expands the overnight range.", false]);
    windows.push([newYorkOpen, addHours(newYorkOpen, 4), "US cash-session flow", "Macro headlines and risk sentiment can lift volatility.", false]);
  }
  return { levels, windows };
}

function formatWindow(start, end, now = new Date()) {
  const hour = value => `${String(Math.floor(value)).padStart(2, "0")}:00`;
  const ukOffset = zoneOffsetHours("Europe/London", now);
  return `${hour(start)}–${hour(end)} UTC · ${hour(addHours(start, ukOffset))}–${hour(addHours(end, ukOffset))} UK`;
}

function renderSessions(setup) {
  const now = new Date();
  $("#utcClock").textContent = now.toLocaleTimeString("en-GB", { timeZone: "UTC", hour12: false });
  $("#sessionCards").innerHTML = sessions.map(session => {
    const parts = zoneParts(now, session.zone);
    const hour = Number(parts.hour);
    const weekday = !["Sat", "Sun"].includes(parts.weekday);
    const open = weekday && hour >= session.open && hour < session.close;
    return `<article class="session-card ${open ? "open" : ""}"><div class="session-card-top"><i class="region-dot"></i><h3>${session.name}</h3><span class="session-status">${open ? "OPEN" : "CLOSED"}</span></div><div class="session-clock">${parts.hour}:${parts.minute}:${parts.second}</div><p>${session.region} · ${session.relevance}</p></article>`;
  }).join("");

  const model = volatilityModel(setup, now);
  const currentHour = now.getUTCHours();
  $("#heatmapTitle").textContent = `${setup.symbol} typical volatility by UTC hour`;
  $("#windowMarket").textContent = `${setup.group} · DST-aware`;
  $("#volatilityHeatmap").innerHTML = model.levels.map((level, hour) => `<span class="heat-cell level-${level} ${hour === currentHour ? "current" : ""}" title="${String(hour).padStart(2,"0")}:00 UTC · ${["","Low","Active","High","Peak"][level]}"></span>`).join("");
  $("#heatmapAxis").innerHTML = model.levels.map((_, hour) => `<span>${hour % 3 === 0 ? String(hour).padStart(2,"0") : ""}</span>`).join("");
  $("#bestWindowList").innerHTML = model.windows.map(window => `<div class="best-window ${window[4] ? "peak" : ""}"><strong>${window[4] ? "PEAK · " : ""}${formatWindow(window[0], window[1], now)}</strong><span>${window[2]} — ${window[3]}</span></div>`).join("");
}

function scenarioData(setup, scenario) {
  const risk = Math.abs(setup.entry - setup.stop);
  if (scenario === "Bullish") return {
    text: `If price confirms above ${formatPrice(setup.entry, setup.decimals)} with displacement, favour continuation toward the external liquidity objective. Trail only after a new protected swing forms.`,
    trigger: setup.direction === "Short" ? setup.stop : setup.entry,
    entry: setup.entry,
    stop: setup.stop,
    target: setup.target
  };
  if (scenario === "Bearish") return {
    text: `If price closes through the protected level at ${formatPrice(setup.stop, setup.decimals)}, the primary thesis is invalid. Stand aside and reassess from the next higher-timeframe zone.`,
    trigger: setup.stop,
    entry: setup.stop,
    stop: setup.entry,
    target: setup.stop - (setup.entry - setup.stop) * 1.5
  };
  return {
    text: `Wait for price to trade into the entry zone, then require a lower-timeframe structure shift in the planned direction. Take partial profit near 1.5R and protect the position before the final target.`,
    trigger: setup.entry,
    entry: setup.entry,
    stop: setup.stop,
    target: setup.target
  };
}

// Computed live from the sibling setups sharing this symbol, rather than
// stored per-setup - each of the 5 timeframes is scanned independently by
// the backend, so this always reflects whatever's currently in memory for
// each one (real result, stale last-known, or never-scanned) instead of a
// snapshot frozen at hydration time.
function biasesFor(symbol) {
  const result = {};
  for (const tf of timeframes) {
    const sibling = setups.find(s => s.symbol === symbol && s.timeframe === tf.label);
    if (!sibling || !sibling.hydrated) { result[tf.label] = "Not yet scanned"; continue; }
    const label = sibling.direction === "Long" ? "Bullish" : sibling.direction === "Short" ? "Bearish" : "No setup";
    result[tf.label] = sibling.stale ? `${label} (last known)` : label;
  }
  return result;
}

function renderInspection() {
  const setup = setups.find(s => s.id === state.selected) || filteredSetups()[0] || setups[0];
  state.selected = setup.id;
  const scenario = scenarioData(setup, state.scenario);
  const liveLevels = overlayLevels(setup);
  const targetY = liveLevels[0].y;
  const entryY = liveLevels[1].y;
  const stopY = liveLevels[2].y;
  const pathColour = setup.direction === "Short" ? "#ff6f78" : "#52d49c";
  // The zone-quality family's label always mentions "order block", "FVG" AND
  // "Willis Zone" together (it's one family covering all three) - only its
  // basis text says which one actually applied, so check that, not the label.
  const zoneEntry = setup.confluences.find(item => /order block, fvg or willis zone quality/i.test(item));
  const zoneBasis = zoneEntry ? zoneEntry.slice(zoneEntry.indexOf(": ") + 2) : "";
  const hasWillis = /willis zone/i.test(zoneBasis);
  const hasOrderBlock = !hasWillis && /valid order block present/i.test(zoneBasis);
  const hasFvg = !hasWillis && !hasOrderBlock && /fair value gap present/i.test(zoneBasis);
  const primaryZoneLabel = hasOrderBlock ? "ORDER BLOCK / ENTRY ZONE" : `${setup.levels[0][0].toUpperCase()} / ENTRY ZONE`;
  const secondaryZoneLabel = hasFvg ? "FVG / IMBALANCE" : hasWillis ? "WILLIS ZONE" : "STRUCTURE CONFIRMATION";
  $("#inspectionPanel").innerHTML = `
    <header class="inspection-head">
      <div class="inspection-title">
        <span class="asset-icon" style="--group-color:${groupMeta[setup.group].color}">${setup.icon}</span>
        <div><h2>${setup.symbol}</h2><p>${setup.name} · ${setup.group}</p></div>
        <div class="inspection-head-actions">
          ${setup.comingSoon ? "" : `
          <button class="watch-button ${state.watchlist.includes(setup.id) ? "selected" : ""}" data-detail-action="watch" type="button">${state.watchlist.includes(setup.id) ? "★ Watching" : "☆ Watch"}</button>
          <button class="compare-button ${state.comparison.includes(setup.id) ? "selected" : ""}" data-detail-action="compare" type="button">⇄ Compare</button>`}
        </div>
      </div>
      ${setup.comingSoon ? `<div class="demo-tag" style="margin:0 0 12px">COMING SOON · ${setup.symbol} analysis isn't wired to a live data source yet</div>` : ""}
      ${!setup.comingSoon && setup.stale ? `<div class="demo-tag" style="margin:0 0 12px">LAST KNOWN RESULT (${timeAgo(setup.cachedAtMs)}) · ${setup.liveError || "Backend did not return a fresh result on the last poll"}</div>` : ""}
      ${!setup.comingSoon && !setup.hydrated ? `<div class="demo-tag" style="margin:0 0 12px">AWAITING FIRST SCAN · No result from the backend yet</div>` : ""}
      <div class="signal-row">
        <div class="signal-stat"><span>Bias</span><strong class="${setup.direction === "Long" ? "positive" : setup.direction === "Short" ? "negative" : ""}">${setup.direction}</strong></div>
        <div class="signal-stat"><span>Timeframe</span><strong>${setup.timeframe}</strong></div>
        <div class="signal-stat"><span>Market Condition</span><strong>${setup.condition}</strong></div>
        <div class="signal-stat"><span>Grade</span><strong>${setup.grade || "—"}</strong></div>
        <div class="signal-stat"><span>Confidence</span><strong>${setup.comingSoon ? "—" : `${setup.score}/100`}</strong></div>
        <div class="signal-stat"><span>Projected R:R</span><strong>${setup.comingSoon ? "—" : `${setup.rr.toFixed(1)}R`}</strong></div>
      </div>
    </header>
    <div class="inspection-body">
      <section>
        <div class="section-heading"><h3>Live price structure</h3><span class="chart-source-note">TradingView · venue delays may apply · ${setup.timeframe}</span></div>
        <div class="chart-wrap" id="chartWrap">
          <div class="live-chart" id="liveChart"><div class="chart-loading">Loading ${setup.symbol} live chart…</div></div>
          <button class="overlay-toggle" id="overlayToggle" type="button">RST ANALYSIS OVERLAY: ${state.overlayVisible ? "ON" : "OFF"}</button>
          <button class="fullscreen-toggle" id="chartFullscreen" type="button" aria-label="View chart and RST markups in full screen">⛶ RST FULL SCREEN</button>
          <span class="fullscreen-hint">RST markups retained · Press Esc to exit</span>
          <div class="analysis-overlay ${state.overlayVisible ? "" : "hidden"}" id="analysisOverlay">
            <span class="overlay-context">⚠ APPROXIMATE OVERLAY - NOT ALIGNED TO TRADINGVIEW'S PRICE SCALE. EXACT PRICE IS LABELLED ON EACH LINE.</span>
            <div class="analysis-zone entry-zone" style="--zone-y:${Math.max(3, entryY - 3)}%"><span>${primaryZoneLabel}</span></div>
            <div class="analysis-zone fvg-zone" style="--zone-y:${Math.max(4, ((entryY + targetY) / 2) - 2)}%"><span>${secondaryZoneLabel}</span></div>
            <svg class="direction-path" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
              <defs><marker id="signalArrow" markerWidth="7" markerHeight="7" refX="5" refY="3.5" orient="auto"><polygon points="0 0, 7 3.5, 0 7" fill="${pathColour}" /></marker></defs>
              <path d="M 56 ${entryY} C 65 ${entryY}, 70 ${targetY}, 80 ${targetY}" stroke="${pathColour}" marker-end="url(#signalArrow)" />
            </svg>
            <span class="liquidity-ring" style="--ring-y:${Math.max(3, targetY - 4)}%"><b>LIQUIDITY OBJECTIVE</b></span>
            <span class="sweep-marker" style="--sweep-y:${Math.min(90, stopY - 2)}%"><b>INVALIDATION / SWEEP ZONE</b></span>
            ${liveLevels.map(level => `<div class="overlay-line" style="--level-y:${level.y}%;--level-color:${level.colour}"><span>${level.label} · ${formatPrice(level.value, setup.decimals)}</span></div>`).join("")}
            ${setup.confluences.slice(0,5).map((item, index) => `<span class="chart-pin" style="--pin-x:${22 + index * 13}%;--pin-y:${18 + ((setup.score + index * 17) % 58)}%" title="${confluenceType(item)}: ${item}">${index + 1}</span>`).join("")}
          </div>
          <aside class="fullscreen-analysis-panel" id="fullscreenAnalysisPanel">
            <header><span>RST VISUAL JUSTIFICATION</span><strong>${setup.symbol} · ${setup.direction.toUpperCase()} · ${setup.score}/100</strong></header>
            <div class="fullscreen-levels">
              <div class="target"><span>Exit / target</span><strong>${formatPrice(setup.target, setup.decimals)}</strong></div>
              <div class="entry"><span>Entry</span><strong>${formatPrice(setup.entry, setup.decimals)}</strong></div>
              <div class="stop"><span>Stop loss</span><strong>${formatPrice(setup.stop, setup.decimals)}</strong></div>
            </div>
            <div class="fullscreen-confluences">${setup.confluences.slice(0,5).map((item, index) => `<div><b>${index + 1}</b><span><em>${confluenceType(item)}</em>${item}</span></div>`).join("")}</div>
            <p>Exact displayed prices. Vertical placement is relative because TradingView’s embedded price scale is isolated from external drawings.</p>
          </aside>
        </div>
        <div class="annotation-legend">${setup.confluences.slice(0,5).map((item, index) => `<div><b>${index + 1}</b><span><em>${confluenceType(item)}</em>${item}</span></div>`).join("")}</div>
        <p class="overlay-caveat">⚠ The entry/stop/target lines are positioned relative to <em>each other</em>, not to TradingView's own price axis - the free embedded chart doesn't expose its price-to-pixel scale to outside code, so exact vertical alignment isn't possible here. Always read the exact price printed on each line's label, not its height on screen.</p>
        <p class="markup-tip">Use <strong>RST Full Screen</strong> to keep the entry, target, stop and confluence layers visible. TradingView’s internal full-screen control can only expand its own embedded chart.</p>
      </section>
      <div class="detail-grid">
        <section class="detail-card">
          <div class="section-heading"><h3>Key levels</h3><span>${setup.levels.length} mapped</span></div>
          <div class="level-list">${setup.levels.map(l => `<div class="level-row"><span>${l[0]}</span><strong>${l[1]}</strong><small>${l[2]}</small></div>`).join("")}</div>
        </section>
        <section class="detail-card">
          <div class="section-heading"><h3>Confluence stack</h3><span>${setup.confluences.length} / 8 families scored</span></div>
          <div class="confluence-list">${setup.confluences.length ? setup.confluences.map(c => `<div class="confluence-item"><i>✓</i><span>${c}</span></div>`).join("") : `<p class="markup-tip">${setup.hydrated ? "No confluence family scored above zero for this setup." : "Awaiting the backend's first scan."}</p>`}</div>
        </section>
        <section class="detail-card reasoning-card">
          <div class="section-heading"><h3>Setup reasoning</h3><span>Top-down analysis</span></div>
          <p>${setup.reasoning}</p>
          <div class="top-down">${Object.entries(biasesFor(setup.symbol)).map(([frame, bias]) => `<div class="bias-cell"><span>${frame}</span><strong class="${bias.includes("Bullish") ? "positive" : bias.includes("Bearish") ? "negative" : ""}">${bias}</strong></div>`).join("")}</div>
        </section>
        <section class="detail-card scenario-card">
          <div class="section-heading"><h3>Entry & exit scenarios</h3><span>Conditional planning</span></div>
          <div class="tab-list">${["Base", "Bullish", "Bearish"].map(tab => `<button class="scenario-tab ${state.scenario === tab ? "active" : ""}" data-scenario="${tab}" type="button">${tab} case</button>`).join("")}</div>
          <div class="scenario-content"><p>${scenario.text}</p><div class="scenario-levels"><div><span>Trigger</span><strong>${formatPrice(scenario.trigger, setup.decimals)}</strong></div><div><span>Entry</span><strong>${formatPrice(scenario.entry, setup.decimals)}</strong></div><div><span>Protective stop</span><strong>${formatPrice(scenario.stop, setup.decimals)}</strong></div><div><span>Exit target</span><strong>${formatPrice(scenario.target, setup.decimals)}</strong></div></div></div>
        </section>
        <section class="detail-card risk-card">
          <div class="section-heading"><h3>Capital-aware risk calculator</h3><span>Default risk: 1% · ${state.scenario} case</span></div>
          <div class="risk-inputs">
            <label>Account balance (£)<input id="balanceInput" type="number" min="1" step="100" value="10000" /></label>
            <label>Risk %<input id="riskInput" type="number" min="0.1" max="2" step="0.1" value="1" /></label>
            <label>Entry<input id="entryInput" type="number" step="any" value="${scenario.entry}" /></label>
            <label>Stop<input id="stopInput" type="number" step="any" value="${scenario.stop}" /></label>
            <label>Target<input id="targetInput" type="number" step="any" value="${scenario.target}" /></label>
          </div>
          <div class="risk-results"><span>Risk amount<strong id="riskAmount">£100.00</strong></span><span>Price-risk units<strong id="positionUnits">—</strong></span><span>Calculated R:R<strong id="calculatedRR">—</strong></span></div>
          <p class="risk-warning" id="riskWarning" hidden></p>
          <p class="disclaimer">Position units are a price-distance illustration and do not account for contract size, leverage, spread, fees, slippage, or broker specifications. Validate every order independently. Risk is capped at 2% in this workspace.</p>
        </section>
      </div>
    </div>`;
  calculateRisk();
  renderLiveChart(setup);
  renderLiveIntelligence(setup);
  renderSessions(setup);
}

function calculateRisk() {
  const setup = setups.find(s => s.id === state.selected);
  const balanceInput = $("#balanceInput");
  const stopInput = $("#stopInput");
  const riskInput = $("#riskInput");

  const rawBalance = Number(balanceInput?.value);
  const balanceInvalid = !!balanceInput && balanceInput.value !== "" && (!Number.isFinite(rawBalance) || rawBalance <= 0);
  const balance = balanceInvalid ? 0 : Math.max(0, rawBalance || 0);
  const riskPct = Math.min(2, Math.max(0, Number(riskInput?.value || 0)));
  const entry = Number($("#entryInput")?.value || 0);
  const stop = Number(stopInput?.value || 0);
  const target = Number($("#targetInput")?.value || 0);

  // The Bearish scenario inverts entry/stop (it plans the invalidation move,
  // not the primary direction), so a stop-vs-entry check has to compare
  // against the effective direction of the case being sized, not always the
  // setup's base direction - otherwise every Bearish case on a Long setup
  // would be flagged as invalid even though it's inverted on purpose.
  const effectiveDirection = state.scenario === "Bearish"
    ? (setup?.direction === "Long" ? "Short" : setup?.direction === "Short" ? "Long" : null)
    : setup?.direction;
  const directionInvalid = !!effectiveDirection && (
    (effectiveDirection === "Long" && stop >= entry) ||
    (effectiveDirection === "Short" && stop <= entry)
  );

  balanceInput?.classList.toggle("input-invalid", balanceInvalid);
  stopInput?.classList.toggle("input-invalid", directionInvalid);

  const riskAmount = balance * riskPct / 100;
  const distance = Math.abs(entry - stop);
  const units = distance ? riskAmount / distance : 0;
  const rr = distance ? Math.abs(target - entry) / distance : 0;

  const warning = $("#riskWarning");
  if (warning) {
    if (balanceInvalid) warning.textContent = "Account balance must be a positive number.";
    else if (directionInvalid) warning.textContent = `Stop must be ${effectiveDirection === "Long" ? "below" : "above"} entry for this ${effectiveDirection.toLowerCase()} case - the current values don't form a valid trade.`;
    else warning.textContent = "";
    warning.hidden = !(balanceInvalid || directionInvalid);
  }

  if ($("#riskAmount")) $("#riskAmount").textContent = balanceInvalid ? "—" : `£${riskAmount.toLocaleString("en-GB", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  if ($("#positionUnits")) $("#positionUnits").textContent = (!balanceInvalid && units) ? units.toLocaleString("en-GB", { maximumFractionDigits: 4 }) : "—";
  if ($("#calculatedRR")) $("#calculatedRR").textContent = (!directionInvalid && rr) ? `${rr.toFixed(2)}R` : "—";
  if (riskInput && Number(riskInput.value) > 2) riskInput.value = 2;
}

function toggleComparison(id) {
  if (state.comparison.includes(id)) state.comparison = state.comparison.filter(x => x !== id);
  else if (state.comparison.length < 3) state.comparison.push(id);
  else return showToast("Compare up to three setups at a time");
  savePersistedIds("rst_comparison", state.comparison);
  renderSetupList();
  renderInspection();
  renderCompareTray();
}

function toggleWatch(id) {
  if (state.watchlist.includes(id)) state.watchlist = state.watchlist.filter(x => x !== id);
  else state.watchlist.push(id);
  savePersistedIds("rst_watchlist", state.watchlist);
  $("#watchCount").textContent = state.watchlist.length;
  renderSetupList();
  renderInspection();
  showToast(state.watchlist.includes(id) ? "Added to watchlist" : "Removed from watchlist");
}

function renderCompareTray() {
  const count = state.comparison.length;
  $("#compareTray").hidden = count === 0;
  $("#compareCount").textContent = count;
  $("#navCompareCount").textContent = count;
  $("#compareChips").innerHTML = state.comparison.map(id => `<span class="compare-chip">${setups.find(s => s.id === id).symbol} · ${setups.find(s => s.id === id).timeframe}</span>`).join("");
  $("#openCompare").disabled = count < 2;
}

function openComparison() {
  if (state.comparison.length < 2) return;
  const selected = state.comparison.map(id => setups.find(s => s.id === id));
  const row = (label, values, renderer = value => `<strong>${value}</strong>`) => `<div class="comparison-row" style="--columns:${selected.length}"><div class="row-label">${label}</div>${values.map(renderer).join("")}</div>`;
  $("#comparisonGrid").innerHTML = [
    row("Market", selected, s => `<div class="comparison-asset"><strong>${s.symbol}</strong><small>${s.group} · ${s.timeframe}</small></div>`),
    row("Bias", selected.map(s => s.direction)),
    row("Market Condition", selected.map(s => s.condition)),
    row("Confidence", selected.map(s => `${s.score}/100`)),
    row("Confluences", selected.map(s => `${s.confluences.length} confirmed`)),
    row("Entry", selected.map(s => formatPrice(s.entry, s.decimals))),
    row("Stop", selected.map(s => formatPrice(s.stop, s.decimals))),
    row("Target", selected.map(s => formatPrice(s.target, s.decimals))),
    row("Projected R:R", selected.map(s => `${s.rr.toFixed(1)}R`)),
    row("Reasoning", selected, s => `<p>${s.reasoning}</p>`)
  ].join("");
  $("#compareModal").hidden = false;
  document.body.style.overflow = "hidden";
}

function closeComparison() {
  $("#compareModal").hidden = true;
  document.body.style.overflow = "";
}

function syncFullscreenButton() {
  const chart = $("#chartWrap");
  const button = $("#chartFullscreen");
  if (!chart || !button) return;
  const expanded = document.fullscreenElement === chart || chart.classList.contains("chart-expanded");
  button.textContent = expanded ? "× EXIT RST FULL SCREEN" : "⛶ RST FULL SCREEN";
  button.setAttribute("aria-label", expanded ? "Exit full screen chart" : "View chart in full screen");
}

async function toggleChartFullscreen() {
  const chart = $("#chartWrap");
  if (!chart) return;

  if (document.fullscreenElement) {
    await document.exitFullscreen?.();
    return;
  }

  if (chart.classList.contains("chart-expanded")) {
    chart.classList.remove("chart-expanded");
    document.body.classList.remove("chart-expanded-open");
    syncFullscreenButton();
    return;
  }

  state.overlayVisible = true;
  $("#analysisOverlay")?.classList.remove("hidden");
  if ($("#overlayToggle")) $("#overlayToggle").textContent = "RST ANALYSIS OVERLAY: ON";

  if (chart.requestFullscreen && document.fullscreenEnabled !== false) {
    try {
      await chart.requestFullscreen();
      return;
    } catch (_) {
      // Some mobile and embedded browsers restrict the native API; use the in-page fallback.
    }
  }

  chart.classList.add("chart-expanded");
  document.body.classList.add("chart-expanded-open");
  syncFullscreenButton();
}

let toastTimer;
function showToast(message) {
  clearTimeout(toastTimer);
  $("#toast").textContent = message;
  $("#toast").classList.add("show");
  toastTimer = setTimeout(() => $("#toast").classList.remove("show"), 1800);
}

function renderSummaryStrip() {
  const scoped = groupFilteredSetups();
  // "Qualified" = the real ConfluenceScorer grade (A+/A/B), not an arbitrary
  // confluence count - see QUALIFIED_GRADES.
  const qualified = scoped.filter(s => QUALIFIED_GRADES.has(s.grade));
  $("#marketCount").textContent = scoped.length;
  $("#qualifiedCount").textContent = qualified.length;
  $("#bullishCount").textContent = scoped.filter(s => s.direction === "Long").length;
  $("#bearishCount").textContent = scoped.filter(s => s.direction === "Short").length;
  $("#averageRR").textContent = qualified.length ? `${(qualified.reduce((sum, s) => sum + s.rr, 0) / qualified.length).toFixed(1)}R` : "—";

  const liveCount = setups.filter(s => s.live).length;
  const tag = $("#liveStatusTag");
  if (tag) {
    tag.textContent = liveCount === 0 ? "DEMONSTRATION DATA" : liveCount === setups.length ? "ALL LIVE" : `${liveCount}/${setups.length} LIVE · REST DEMO`;
    tag.classList.toggle("live-tag", liveCount > 0);
  }
}

function refresh() {
  renderSummaryStrip();
  renderMarketNav();
  renderTimeframes();
  renderSetupList();
  const watchCount = $("#watchCount");
  if (watchCount) watchCount.textContent = state.watchlist.length;
  const results = filteredSetups();
  if (results.length && !results.some(s => s.id === state.selected)) state.selected = results[0].id;
  renderInspection();
}

function initApp() {
  document.addEventListener("click", event => {
    const scrollTarget = event.target.closest("[data-scroll]");
    if (scrollTarget) {
      const destination = document.getElementById(scrollTarget.dataset.scroll);
      if (destination) destination.scrollIntoView({ behavior: "smooth", block: "start" });
      state.view = "all";
      $$(".nav-item").forEach(item => item.classList.remove("active"));
      scrollTarget.classList.add("active");
      $(".sidebar").classList.remove("open");
      refresh();
      return;
    }
    const navView = event.target.closest("[data-view]");
    if (navView) {
      // Compare/Watchlist are real views: they filter the Setups list to the
      // comparison-selected / starred ids (see filteredSetups()), and the
      // sidebar highlight follows the click like the scroll tabs above.
      // Clicking the same tab again returns to normal market/timeframe browsing.
      state.view = state.view === navView.dataset.view ? "all" : navView.dataset.view;
      $$(".nav-item").forEach(item => item.classList.remove("active"));
      (state.view === "all" ? $('[data-scroll="workspace"]') : navView).classList.add("active");
      refresh();
      $(".sidebar").classList.remove("open");
      return;
    }
    const fullscreenToggle = event.target.closest("#chartFullscreen");
    if (fullscreenToggle) {
      toggleChartFullscreen();
      return;
    }
    const overlayToggle = event.target.closest("#overlayToggle");
    if (overlayToggle) {
      state.overlayVisible = !state.overlayVisible;
      $("#analysisOverlay")?.classList.toggle("hidden", !state.overlayVisible);
      overlayToggle.textContent = `RST ANALYSIS OVERLAY: ${state.overlayVisible ? "ON" : "OFF"}`;
      return;
    }
    const catalyst = event.target.closest("[data-catalyst]");
    if (catalyst) {
      state.catalyst = catalyst.dataset.catalyst;
      $$("[data-catalyst]").forEach(button => button.classList.toggle("active", button === catalyst));
      showToast(`Catalyst check: ${state.catalyst}`);
      return;
    }
    const market = event.target.closest("[data-market]");
    if (market) {
      state.market = market.dataset.market;
      if (state.view !== "all") {
        state.view = "all";
        $$(".nav-item").forEach(item => item.classList.remove("active"));
        $('[data-scroll="workspace"]')?.classList.add("active");
      }
      refresh();
      return;
    }
    const timeframe = event.target.closest("[data-timeframe]");
    if (timeframe) {
      state.timeframe = timeframe.dataset.timeframe;
      savePersistedTimeframe(state.timeframe);
      if (state.view !== "all") {
        state.view = "all";
        $$(".nav-item").forEach(item => item.classList.remove("active"));
        $('[data-scroll="workspace"]')?.classList.add("active");
      }
      refresh();
      return;
    }
    const setupCard = event.target.closest("[data-setup]");
    if (setupCard) {
      const action = event.target.closest("[data-action]")?.dataset.action;
      if (action === "watch") toggleWatch(setupCard.dataset.setup);
      else if (action === "compare") toggleComparison(setupCard.dataset.setup);
      else { state.selected = setupCard.dataset.setup; state.scenario = "Base"; renderSetupList(); renderInspection(); }
      return;
    }
    const detailAction = event.target.closest("[data-detail-action]");
    if (detailAction) detailAction.dataset.detailAction === "watch" ? toggleWatch(state.selected) : toggleComparison(state.selected);
    const scenario = event.target.closest("[data-scenario]");
    if (scenario) { state.scenario = scenario.dataset.scenario; renderInspection(); }
  });

  $("#conditionFilter").addEventListener("change", event => { state.condition = event.target.value; refresh(); });
  $("#sortSelect").addEventListener("change", event => { state.sort = event.target.value; renderSetupList(); });
  $("#searchInput").addEventListener("input", event => { state.search = event.target.value; refresh(); });
  $("#inspectionPanel").addEventListener("input", event => { if (event.target.matches("#balanceInput,#riskInput,#entryInput,#stopInput,#targetInput")) calculateRisk(); });
  $("#openCompare").addEventListener("click", openComparison);
  $("#closeModal").addEventListener("click", closeComparison);
  $("#compareModal").addEventListener("click", event => { if (event.target === $("#compareModal")) closeComparison(); });
  $("#closeCompare").addEventListener("click", () => { state.comparison = []; renderSetupList(); renderInspection(); renderCompareTray(); });
  $("#trackerNavItem").addEventListener("click", () => { openSignalTracker(); $(".sidebar").classList.remove("open"); });
  $("#closeTracker").addEventListener("click", closeSignalTracker);
  $("#trackerModal").addEventListener("click", event => { if (event.target === $("#trackerModal")) closeSignalTracker(); });
  $("#trackerTabLedger").addEventListener("click", () => {
    $("#trackerTabLedger").classList.add("active");
    $("#trackerTabStats").classList.remove("active");
    $("#trackerLedgerView").hidden = false;
    $("#trackerStatsView").hidden = true;
  });
  $("#trackerTabStats").addEventListener("click", () => {
    $("#trackerTabStats").classList.add("active");
    $("#trackerTabLedger").classList.remove("active");
    $("#trackerStatsView").hidden = false;
    $("#trackerLedgerView").hidden = true;
  });
  $("#trackerStatusFilter").addEventListener("change", renderTrackerRows);
  $("#trackerSearch").addEventListener("input", renderTrackerRows);
  $("#trackerSelectAll").addEventListener("change", event => {
    const ids = filteredTrackerEntries().map(e => e.id);
    if (event.target.checked) ids.forEach(id => trackerSelectedIds.add(id));
    else ids.forEach(id => trackerSelectedIds.delete(id));
    renderTrackerRows();
  });
  $("#trackerBody").addEventListener("change", event => {
    const checkbox = event.target.closest(".tracker-row-check");
    if (!checkbox) return;
    if (checkbox.checked) trackerSelectedIds.add(checkbox.dataset.id);
    else trackerSelectedIds.delete(checkbox.dataset.id);
    updateTrackerSelectionUi();
  });
  $("#trackerBody").addEventListener("click", event => {
    const deleteBtn = event.target.closest("[data-delete-row]");
    if (deleteBtn) deleteTrackerRow(deleteBtn.dataset.deleteRow);
  });
  $("#deleteSelectedRows").addEventListener("click", deleteSelectedTrackerRows);
  $("#resetTracker").addEventListener("click", resetTrackerLedger);
  $("#closeConfirm").addEventListener("click", closeConfirmModal);
  $("#cancelConfirm").addEventListener("click", closeConfirmModal);
  $("#confirmModal").addEventListener("click", event => { if (event.target === $("#confirmModal")) closeConfirmModal(); });
  $("#proceedConfirm").addEventListener("click", async () => {
    const action = pendingConfirmAction;
    closeConfirmModal();
    if (action) await action();
  });
  $("#passphraseForm").addEventListener("submit", event => { event.preventDefault(); submitPassphrase(); });
  $("#cancelPassphrase").addEventListener("click", closePassphraseModal);
  $("#closePassphrase").addEventListener("click", closePassphraseModal);
  $("#passphraseModal").addEventListener("click", event => { if (event.target === $("#passphraseModal")) closePassphraseModal(); });
  $("#passphraseInput").addEventListener("input", () => {
    $("#passphraseInput").classList.remove("input-invalid");
    $("#passphraseError").hidden = true;
  });
  $("#mobileMenu").addEventListener("click", () => $(".sidebar").classList.toggle("open"));
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      closeComparison();
      closeSignalTracker();
      closePassphraseModal();
      closeConfirmModal();
      $(".sidebar").classList.remove("open");
      const chart = $("#chartWrap");
      if (chart?.classList.contains("chart-expanded")) {
        chart.classList.remove("chart-expanded");
        document.body.classList.remove("chart-expanded-open");
        syncFullscreenButton();
      }
    }
  });
  document.addEventListener("fullscreenchange", syncFullscreenButton);
  document.addEventListener("fullscreenerror", () => showToast("Full screen is restricted by this browser"));

  restoreSetupCache();
  refresh();

  if (IS_STATIC_DEPLOYMENT) {
    // No live backend to call at all - a scheduled GitHub Actions workflow
    // is the only thing that ever talks to Bybit/Twelve Data/Alpha
    // Vantage/Finnhub. Polling the static file itself is essentially free
    // (no provider quota involved), so this can check fairly often even
    // though the underlying data only actually changes once per scheduled run.
    fetchStaticSnapshot();
    setInterval(fetchStaticSnapshot, 2 * 60 * 1000);
  } else {
    fetchSignals("/api/signals/crypto");
    markPolled("rst_last_crypto_fetch");
    setInterval(hydrateCryptoPoll, CRYPTO_POLL_MS);

    // Stagger the initial per-timeframe FX fetches (each already paces its own
    // instruments 3s apart server-side) so all 5 don't hit Twelve Data's
    // ~8 req/min/key cap at once on first load.
    (async () => {
      let first = true;
      for (const tf of FX_TIMEFRAMES) {
        if (!first) await new Promise(resolve => setTimeout(resolve, 5000));
        first = false;
        await fetchSignals(`/api/signals/fx?timeframe=${encodeURIComponent(tf)}`);
        markPolled(`rst_last_fx_fetch_${tf}`);
      }
    })();
    for (const tf of FX_TIMEFRAMES) setInterval(() => hydrateFxPoll(tf), FX_POLL_MS_BY_TIMEFRAME[tf]);
  }

  renderCompareTray();
  setInterval(() => {
    const selected = setups.find(setup => setup.id === state.selected) || setups[0];
    renderSessions(selected);
  }, 30000);
}

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent implements AfterViewInit {
  ngAfterViewInit(): void {
    initApp();
  }
}
