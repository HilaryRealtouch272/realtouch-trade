"""Private signed signal feed -> Exness MT5 execution. Defaults to demo only.

Use desktop MT5 on the same Windows machine. Credentials stay in MT5.
Only this process may use its execution database (OS lock enforced in main).
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone, timedelta
from decimal import Decimal, ROUND_FLOOR
import hashlib
import json
import logging
import math
from pathlib import Path
import sqlite3
import time
from signal_feed import FeedError, SignedSignalFeed

UTC = timezone.utc
LOG = logging.getLogger("exness-demo")


class Blocked(Exception):
    pass


class ReversalPending(Blocked):
    pass


def initialize_terminal(mt5, config):
    options = {"portable": config.get("terminalPortable", config.get("mode") == "live")}
    connected = mt5.initialize(config["terminalPath"], **options) if config.get("terminalPath") else mt5.initialize(**options)
    if not connected:
        error = mt5.last_error()
        mt5.shutdown()  # Reset this process's IPC connection, not the terminal/account.
        raise Blocked(f"MT5 connection failed (code {error[0]}); check the configured {config.get('mode')} terminal and Windows session")


def write_worker_status(path, state, reason=""):
    data = dict(state=state, checkedAtUtc=datetime.now(UTC).isoformat(), reason=reason)
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(data), encoding="utf-8")
    temp.replace(path)


def utc(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise Blocked("Timestamp must include UTC offset")
    return parsed.astimezone(UTC)


def positive(value):
    number = float(value)
    if not math.isfinite(number) or number <= 0:
        raise Blocked("Expected a finite positive number")
    return number


def volume_down(value, step):
    return float((Decimal(str(value)) / Decimal(str(step))).to_integral_value(
        rounding=ROUND_FLOOR) * Decimal(str(step)))


def validate_intent(s, now):
    if s["flow"] not in ("main", "vtb") or s["side"] not in ("Long", "Short"):
        raise Blocked("Unknown strategy or direction")
    if not isinstance(s["id"], str) or not s["id"].startswith(s["flow"] + ":"):
        raise Blocked("Invalid signal identity")
    qualified, deadline, close_by = (utc(s[k]) for k in
                                    ("qualifiedAtUtc", "entryDeadlineUtc", "closeByUtc"))
    max_age = timedelta(minutes=15 if s["flow"] == "vtb" else 5)
    telegram_policy = s.get("entryPolicy") == "telegram"
    if qualified > now or now > deadline or (not telegram_policy and now - qualified > max_age):
        raise Blocked("Stale, expired or future signal; never enter historical alerts")
    if deadline < qualified or (not telegram_policy and deadline - qualified > max_age) or close_by <= now:
        raise Blocked("Invalid signal lifetime")
    if telegram_policy and (deadline != close_by or s.get("contractVersion") != 2):
        raise Blocked("Invalid Telegram entry contract")
    if (now + timedelta(hours=1)).weekday() == 6:
        raise Blocked("Sunday in Lagos: existing positions only")
    if not s["symbol"].upper().endswith("/USDT"):
        if ((now + timedelta(hours=1)).weekday() >= 5 or now.weekday() == 5
                or (now.weekday() == 4 and now.hour >= 22)
                or (now.weekday() == 6 and now.hour < 22)):
            raise Blocked("Non-crypto market closed: existing positions only")
    entry, stop = positive(s["entry"]), positive(s["stop"])
    lo, hi = positive(s["zoneMin"]), positive(s["zoneMax"])
    if not lo <= entry <= hi or not 0 < positive(s["riskMultiplier"]) <= 1:
        raise Blocked("Invalid zone or risk multiplier")
    targets = [positive(t) for t in s["targets"]]
    if len(targets) != (3 if s["flow"] == "main" else 1):
        raise Blocked("Unexpected target count")
    direction = 1 if s["side"] == "Long" else -1
    if direction * (entry - stop) <= 0 or any(direction * (t - entry) <= 0 for t in targets):
        raise Blocked("Invalid stop/target geometry")
    if any(direction * (b - a) <= 0 for a, b in zip(targets, targets[1:])):
        raise Blocked("Targets out of order")
    replaces = s.get("replaces") or []
    if (not isinstance(replaces, list) or len(replaces) > 8 or
            any(not isinstance(identity, str) or not identity.startswith("vtb:") or identity == s["id"] for identity in replaces)
            or (replaces and s["flow"] != "vtb")):
        raise Blocked("Invalid VTB reversal references")


class Store:
    def __init__(self, path):
        self.db = sqlite3.connect(path, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS signals(
                id TEXT PRIMARY KEY, payload TEXT NOT NULL, status TEXT NOT NULL,
                reason TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS legs(
                id TEXT PRIMARY KEY, signal_id TEXT NOT NULL, magic INTEGER UNIQUE NOT NULL,
                symbol TEXT NOT NULL, status TEXT NOT NULL, ticket INTEGER, position_id INTEGER,
                volume REAL, target REAL, entry_price REAL, stop REAL,
                opened_at INTEGER, closed_at INTEGER, pnl REAL, reason TEXT NOT NULL DEFAULT '');
        """)

    def remember(self, s):
        return self.db.execute("INSERT OR IGNORE INTO signals(id,payload,status) VALUES(?,?,'Pending')",
                               (s["id"], json.dumps(s))).rowcount == 1

    def signal_status(self, identity, status, reason=""):
        self.db.execute("UPDATE signals SET status=?,reason=? WHERE id=?", (status, reason, identity))

    def leg_status(self, identity, status, reason="", **fields):
        fields.update(status=status, reason=reason)
        self.db.execute("UPDATE legs SET " + ",".join(k + "=?" for k in fields) + " WHERE id=?",
                        (*fields.values(), identity))

    def blocked(self):
        return self.db.execute("SELECT 1 FROM legs WHERE status IN ('Prepared','Uncertain') LIMIT 1").fetchone() is not None


class Worker:
    def __init__(self, mt5, config, store, source=None, allow_real_orders=False):
        self.mt = mt5
        self.c = config
        self.store = store
        self.source = source
        self.allow_real_orders = allow_real_orders
        self.main_entry_policy = config.get("mainEntryPolicy", "drift-and-zone")
        self.telegram_execution = config.get("entryExecutionPolicy", "legacy") == "telegram"
        if config.get("entryExecutionPolicy", "legacy") not in ("legacy", "telegram"):
            raise Blocked("Unknown execution policy")
        if self.main_entry_policy not in ("drift-and-zone", "zone"):
            raise Blocked("mainEntryPolicy must be drift-and-zone or zone")
        self.allowed_flows = config.get("allowedFlows", ["main", "vtb"])
        if (not isinstance(self.allowed_flows, list) or not self.allowed_flows
                or any(flow not in ("main", "vtb") for flow in self.allowed_flows)):
            raise Blocked("allowedFlows must select main and/or vtb")
        if config.get("mode") not in ("demo", "live"):
            raise Blocked("Execution mode must be demo or live")
        if config["mode"] == "live":
            if (config.get("sizingMode") != "fixed" or config.get("fixedLots") != .01
                    or config.get("mainFixedLots") != .01 or config.get("mainExitMode") != "tp2"):
                raise Blocked("Live profile requires 0.01 lots and main TP2 without partial exits")
        limits = [("riskPercent", 0.25), ("maxOpenRiskPercent", 0.5), ("dailyLossPercent", 1)]
        if not self.telegram_execution:
            limits += [("maxEntryDriftR", 0.1), ("maxSpreadR", 0.1)]
        for key, ceiling in limits:
            if positive(config[key]) > ceiling:
                raise Blocked(f"{key} exceeds this demo implementation's limit")
        if not self.telegram_execution and not 0.8 <= positive(config["minimumRewardRisk"]) <= 10:
            raise Blocked("Invalid minimum reward/risk")
        if not 0 <= int(config["deviationPoints"]) <= 20:
            raise Blocked("Invalid deviation allowance")

    def account(self):
        a, terminal = self.mt.account_info(), self.mt.terminal_info()
        if a is None or terminal is None or not terminal.connected:
            raise Blocked("MT5 is disconnected")
        mode = self.mt.ACCOUNT_TRADE_MODE_DEMO if self.c["mode"] == "demo" else self.mt.ACCOUNT_TRADE_MODE_REAL
        if a.trade_mode != mode or "exness" not in a.company.lower():
            raise Blocked("Connected account type does not match the configured Exness mode")
        if (not self.c.get("expectedLogin") or not self.c.get("expectedServer") or
                a.login != int(self.c["expectedLogin"]) or a.server != self.c["expectedServer"]):
            raise Blocked("Configured login/server do not match the connected account")
        if a.margin_mode != self.mt.ACCOUNT_MARGIN_MODE_RETAIL_HEDGING:
            raise Blocked("An Exness hedging account is required for this worker")
        binding = f"{a.login}@{a.server}"
        row = self.store.db.execute("SELECT value FROM meta WHERE key='account'").fetchone()
        if row and row[0] != binding:
            raise Blocked("Execution database belongs to a different account")
        self.store.db.execute("INSERT OR IGNORE INTO meta VALUES('account',?)", (binding,))
        return a, terminal

    def submission_permission(self):
        if self.c["mode"] == "live" and not self.allow_real_orders:
            raise Blocked("Real orders require the owner's explicit --allow-real-orders launch option")

    def positions(self):
        positions = self.mt.positions_get()
        if positions is None:
            raise Blocked("Cannot read broker positions")
        return positions

    def quote(self, symbol, now):
        if not self.mt.symbol_select(symbol, True):
            raise Blocked("Configured broker symbol is unavailable")
        # A newly subscribed symbol can briefly have no tick. Wait at most three
        # seconds for an executable quote; never relax the freshness requirement.
        started = time.monotonic()
        for attempt in range(13):
            info, tick = self.mt.symbol_info(symbol), self.mt.symbol_info_tick(symbol)
            current_time = now.timestamp() + time.monotonic() - started
            if (info is not None and tick is not None and abs(current_time - tick.time) <= 30
                    and math.isfinite(tick.ask) and math.isfinite(tick.bid) and tick.ask > 0 and tick.bid > 0):
                if tick.ask < tick.bid:
                    raise Blocked("Invalid broker spread")
                return info, tick
            if attempt < 12:
                time.sleep(.25)
        raise Blocked("Missing or stale broker quote after subscription warm-up")

    def filling(self, info):
        if info.filling_mode & 2:
            return self.mt.ORDER_FILLING_IOC
        if info.filling_mode & 1:
            return self.mt.ORDER_FILLING_FOK
        raise Blocked("Broker must support IOC or FOK; resting partial-entry orders are not supported")

    def risk(self, positions):
        total = 0.0
        for p in positions:
            if p.sl <= 0:
                raise Blocked("An account position has no stop; new entries blocked")
            loss = self.mt.order_calc_profit(p.type, p.symbol, p.volume, p.price_open, p.sl)
            if loss is None or not math.isfinite(loss):
                raise Blocked("Cannot calculate existing account risk")
            total += max(0, -loss)
        return total

    def daily_guard(self, a, positions, now):
        if self.c.get("sizingMode") == "fixed":
            return  # Owner-selected fixed lots bypass cash-risk sizing limits.
        # Whole account, Lagos calendar day, including commission, swap and floating loss.
        local = now + timedelta(hours=1)
        start = local.replace(hour=0, minute=0, second=0, microsecond=0) - timedelta(hours=1)
        deals = self.mt.history_deals_get(start, now)
        if deals is None:
            raise Blocked("Cannot read daily broker history")
        trading = [d for d in deals if d.type in (self.mt.DEAL_TYPE_BUY, self.mt.DEAL_TYPE_SELL)]
        realized = sum(d.profit + d.commission + d.swap + d.fee for d in trading)
        day_balance = a.balance - realized
        if day_balance <= 0 or realized + sum(p.profit + p.swap for p in positions) <= -day_balance * self.c["dailyLossPercent"] / 100:
            raise Blocked("Daily account loss limit reached")

    def plan(self, s, a, positions, now):
        validate_intent(s, now)
        if s["flow"] not in self.allowed_flows:
            raise Blocked("Signal strategy is not assigned to this account")
        symbol = self.c["symbols"].get(s["symbol"])
        if self.c["mode"] == "live" and (s["symbol"].upper() == "WTI/USD"
                or any(name in (symbol or "").upper() for name in ("USOIL", "WTI"))):
            raise Blocked("WTI is excluded from the real-account profile")
        if not symbol:
            raise Blocked("No explicit Exness symbol mapping; never guess suffixes")
        if any(p.symbol == symbol for p in positions):
            raise Blocked("A position already exists in this symbol, across either flow")
        orders = self.mt.orders_get()
        if orders is None or (orders and (not self.telegram_execution or
                any(not getattr(order, "symbol", None) or order.symbol == symbol for order in orders))):
            raise Blocked("Cannot verify pending orders or account has pending orders")
        self.daily_guard(a, positions, now)
        info, tick = self.quote(symbol, now)
        long = s["side"] == "Long"
        direction = 1 if long else -1
        action = self.mt.ORDER_TYPE_BUY if long else self.mt.ORDER_TYPE_SELL
        price = tick.ask if long else tick.bid
        reference_risk = abs(s["entry"] - s["stop"])
        if self.entry_drift_exceeded(s, price):
            raise Blocked(f"Broker entry has drifted too far from the qualified entry: broker={price:.8f}, "
                          f"reference={s['entry']:.8f}, drift={abs(price-s['entry']):.8f}, "
                          f"allowed={reference_risk*self.c['maxEntryDriftR']:.8f}, "
                          f"zone={s['zoneMin']:.8f}..{s['zoneMax']:.8f}")
        if s["flow"] == "main" and not s["zoneMin"] <= price <= s["zoneMax"]:
            raise Blocked(f"Broker entry is outside the qualified zone: broker={price:.8f}, "
                          f"zone={s['zoneMin']:.8f}..{s['zoneMax']:.8f}")
        if not self.telegram_execution and tick.ask - tick.bid > reference_risk * self.c["maxSpreadR"]:
            raise Blocked("Broker spread exceeds the limit")
        grid = positive(info.trade_tick_size)
        stop = round(round(s["stop"] / grid) * grid, info.digits)
        targets = [round(round(t / grid) * grid, info.digits) for t in s["targets"]]
        single_tp2 = s["flow"] == "main" and self.c.get("sizingMode") == "fixed" and self.c.get("mainExitMode") == "tp2"
        if single_tp2:
            targets = [targets[1]]
        # Native stops must be valid against the closing side of the spread too.
        closing_price = tick.bid if long else tick.ask
        distance = max(info.trade_stops_level * info.point, grid)
        if direction * (closing_price - stop) < distance or any(
                direction * (t - closing_price) < distance for t in targets):
            raise Blocked("Stop or target already crossed / too close for the broker")
        if not self.telegram_execution and min(direction * (t - price) for t in targets) / abs(price - stop) < self.c["minimumRewardRisk"]:
            raise Blocked("Insufficient remaining reward/risk")
        positive(a.equity)
        budget = a.equity * self.c["riskPercent"] / 100 * s["riskMultiplier"] * 0.90
        loss = self.mt.order_calc_profit(action, symbol, 1.0, price, stop)
        if loss is None or not math.isfinite(loss) or loss >= 0:
            raise Blocked("Cannot calculate stop loss in account currency")
        weights = (0.25, 0.50, 0.25) if s["flow"] == "main" else (1.0,)
        fixed = self.c.get("sizingMode") == "fixed"
        if single_tp2:
            volumes = [positive(self.c["mainFixedLots"])]
        elif fixed and s["flow"] == "main":
            if self.c.get("mainExitMode") != "tp1_tp2":
                raise Blocked("Main demo entries paused until the target/partial-exit choice is confirmed")
            total = positive(self.c["mainFixedLots"])
            volumes = [total / 2, total / 2]
            targets = targets[:2]
        else:
            volumes = [positive(self.c["fixedLots"])] if fixed else [volume_down(budget * w / abs(loss), positive(info.volume_step)) for w in weights]
        if any(abs(v / info.volume_step - round(v / info.volume_step)) > 1e-7 for v in volumes):
            raise Blocked("Requested fixed lot size is not supported by the broker")
        if any(v < positive(info.volume_min) or v > positive(info.volume_max) for v in volumes):
            raise Blocked(f"Requested lot sizes {volumes} are outside broker minimum/maximum: "
                          f"min={info.volume_min}, max={info.volume_max}, step={info.volume_step}")
        planned = sum(volumes) * abs(loss)
        if not fixed and self.risk(positions) + planned > a.equity * self.c["maxOpenRiskPercent"] / 100:
            raise Blocked("Combined account stop risk would exceed the limit")
        requests = []
        for index, (volume, target) in enumerate(zip(volumes, targets)):
            leg_id = s["id"] + f":{index}"
            magic = int.from_bytes(hashlib.sha256(leg_id.encode()).digest()[:4], "big") & 0x7fffffff
            requests.append((leg_id, {
                "action": self.mt.TRADE_ACTION_DEAL, "symbol": symbol,
                "volume": volume, "type": action, "price": price, "sl": stop, "tp": target,
                "deviation": int(self.c["deviationPoints"]), "magic": magic,
                "comment": "RT-" + hashlib.sha256(leg_id.encode()).hexdigest()[:20],
                "type_time": self.mt.ORDER_TIME_GTC, "type_filling": self.filling(info)}))
        margins = [self.mt.order_calc_margin(action, symbol, v, price) for v in volumes]
        margin_available = a.margin_free if self.telegram_execution else a.margin_free * 0.9
        if any(m is None or not math.isfinite(m) or m < 0 for m in margins) or sum(margins) > margin_available:
            raise Blocked("Insufficient free margin")
        for _, request in requests:
            checked = self.mt.order_check(request)
            if checked is None or checked.retcode != 0:
                raise Blocked("Broker order preflight rejected a target leg")
        return requests

    def entry_drift_exceeded(self, signal, price):
        if self.telegram_execution:
            return False
        if signal["flow"] == "main" and self.main_entry_policy == "zone":
            return False  # Main's published entry zone is its price boundary.
        return abs(price - signal["entry"]) > abs(signal["entry"] - signal["stop"]) * self.c["maxEntryDriftR"]

    def execute(self, s, now):
        self.submission_permission()
        started = time.monotonic()
        a, terminal = self.account()
        if not self.c.get("entriesEnabled"):
            raise Blocked("New entries are paused")
        if self.store.blocked():
            raise Blocked("Unresolved broker submission; inspect before any new entry")
        if not terminal.trade_allowed or not a.trade_allowed or not a.trade_expert:
            raise Blocked("MT5 algorithmic trading is disabled")
        self.require_active_intent(s, now)
        self.prepare_reversal(s, a, now)
        requests = self.plan(s, a, self.positions(), now)
        for leg_id, request in requests:
            self.store.db.execute("INSERT INTO legs(id,signal_id,magic,symbol,status,volume,target) VALUES(?,?,?,?,'Planned',?,?)",
                                  (leg_id, s["id"], request["magic"], request["symbol"], request["volume"], request["tp"]))
        for leg_index, (leg_id, request) in enumerate(requests):
            # Persist BEFORE crossing the broker boundary. Never automatically resend
            # a Prepared/Uncertain leg after timeouts or process/machine failures.
            a, _ = self.account()
            send_now = now + timedelta(seconds=time.monotonic() - started)
            validate_intent(s, send_now)
            self.require_active_intent(s, send_now)
            # Check each leg against a fresh executable quote. Preflight estimates
            # do not authorize chasing a price during a slow broker submission.
            info, tick = self.quote(request["symbol"], send_now)
            current = tick.ask if s["side"] == "Long" else tick.bid
            if (self.entry_drift_exceeded(s, current)
                    or (not self.telegram_execution and tick.ask - tick.bid > abs(s["entry"] - s["stop"]) * self.c["maxSpreadR"])
                    or (s["flow"] == "main" and not s["zoneMin"] <= current <= s["zoneMax"])):
                self.store.leg_status(leg_id, "Cancelled", "Quote drifted before submission")
                break
            request["price"] = current
            positions = self.positions()
            self.daily_guard(a, positions, send_now)
            per_lot = self.mt.order_calc_profit(request["type"], request["symbol"], 1.0, current, request["sl"])
            if per_lot is None or not math.isfinite(per_lot) or per_lot >= 0:
                raise Blocked("Cannot recheck stop risk before submission")
            weight = (0.25, 0.50, 0.25)[leg_index] if s["flow"] == "main" else 1.0
            leg_budget = a.equity * self.c["riskPercent"] / 100 * s["riskMultiplier"] * weight * 0.9
            fixed = self.c.get("sizingMode") == "fixed"
            if not fixed:
                request["volume"] = min(request["volume"], volume_down(leg_budget / abs(per_lot), info.volume_step))
            if (request["volume"] < info.volume_min or (not fixed and self.risk(positions) + request["volume"] * abs(per_lot)
                    > a.equity * self.c["maxOpenRiskPercent"] / 100)):
                self.store.leg_status(leg_id, "Cancelled", "Updated broker risk exceeds budget")
                break
            check = self.mt.order_check(request)
            if check is None or check.retcode != 0:
                self.store.leg_status(leg_id, "Cancelled", "Broker preflight changed before submission")
                break
            self.store.leg_status(leg_id, "Prepared")
            try:
                result = self.mt.order_send(request)
            except Exception:
                self.store.leg_status(leg_id, "Uncertain", "Submission raised; reconcile broker history")
                break
            if result is None or result.retcode not in (self.mt.TRADE_RETCODE_DONE, self.mt.TRADE_RETCODE_DONE_PARTIAL):
                self.store.leg_status(leg_id, "Uncertain", "Broker response not a confirmed fill; no automatic retry")
                break
            self.store.leg_status(leg_id, "Prepared", "Fill accepted; awaiting broker position reconciliation")
        self.store.db.execute("UPDATE legs SET status='Cancelled' WHERE signal_id=? AND status='Planned'", (s["id"],))
        active = self.store.db.execute("SELECT 1 FROM legs WHERE signal_id=? AND status IN ('Prepared','Uncertain','Open')",
                                       (s["id"],)).fetchone()
        self.store.signal_status(s["id"], "Tracking" if active else "Rejected",
                                 "" if active else "No legs submitted; broker conditions changed")

    def require_active_intent(self, signal, now):
        if signal.get("entryPolicy") == "telegram":
            if not self.telegram_execution or self.source is None or not self.source.authorizes(signal, now):
                raise Blocked("No current signed active Telegram instruction; no new entry authorized")
        elif self.telegram_execution:
            raise Blocked("Legacy signal is not eligible for the Telegram execution policy")

    def prepare_reversal(self, s, account, now):
        validate_intent(s, now)
        references = s.get("replaces") or []
        if not references:
            return
        positions = self.positions()
        target_rows = []
        for row in self.store.db.execute("SELECT legs.*, signals.payload FROM legs JOIN signals ON legs.signal_id=signals.id WHERE legs.status IN ('Open','Prepared','Uncertain')"):
            if row["signal_id"] not in references:
                continue
            old = json.loads(row["payload"])
            if old["flow"] != "vtb" or old["symbol"] != s["symbol"] or old["side"] == s["side"]:
                raise Blocked("Reversal cannot close a manual/main/unrelated position")
            target_rows.append(row)
        if not target_rows:
            return  # Normal plan still blocks any untracked, manual or main exposure.
        tickets = {row["ticket"] for row in target_rows}
        closing = [p for p in positions if p.ticket in tickets]
        if not closing:
            raise ReversalPending("Awaiting broker-confirmed VTB closure")
        # Preflight the replacement before closing anything. Existing unrelated
        # positions, stale entry, invalid stop, WTI, drift or margin still block it.
        self.plan(s, account, [p for p in positions if p.ticket not in tickets], now)
        for row in target_rows:
            position = next((p for p in closing if p.ticket == row["ticket"] and p.magic == row["magic"] and p.symbol == row["symbol"]), None)
            if position is None:
                raise Blocked("Reversal position identity cannot be verified")
            key = "reversal-close:" + row["id"]
            if self.store.db.execute("INSERT OR IGNORE INTO meta(key,value) VALUES(?,?)", (key, s["id"])).rowcount:
                self.store.leg_status(row["id"], "Uncertain", "VTB reversal close requested; awaiting broker confirmation")
                self.close(position, now)
        # Persist across restarts. Never repeat an ambiguous close or enter the
        # opposite trade before broker history confirms the old position is gone.
        raise ReversalPending("Awaiting broker-confirmed VTB closure")

    def close(self, p, now):
        self.submission_permission()
        # Ticket-scoped close: a later poll cannot open a reverse position.
        self.account()
        info, tick = self.quote(p.symbol, now)
        request = {"action": self.mt.TRADE_ACTION_DEAL, "position": p.ticket,
                   "symbol": p.symbol, "volume": p.volume,
                   "type": self.mt.ORDER_TYPE_SELL if p.type == self.mt.ORDER_TYPE_BUY else self.mt.ORDER_TYPE_BUY,
                   "price": tick.bid if p.type == self.mt.ORDER_TYPE_BUY else tick.ask,
                   "magic": p.magic, "comment": "RT time/protection exit",
                   "deviation": int(self.c["deviationPoints"]), "type_time": self.mt.ORDER_TIME_GTC,
                   "type_filling": self.filling(info)}
        self.mt.order_send(request)

    def reconcile(self, now):
        self.account()
        # Planned legs can only be leftovers from interruption before submission.
        # They are cancelled, never replayed on restart.
        self.store.db.execute("UPDATE legs SET status='Cancelled' WHERE status='Planned'")
        positions = self.positions()
        rows = self.store.db.execute("""SELECT legs.*, signals.payload FROM legs
            JOIN signals ON signals.id=legs.signal_id
            WHERE legs.status IN ('Open','Prepared','Uncertain')""").fetchall()
        for row in rows:
            matches = [p for p in positions if p.magic == row["magic"] and p.symbol == row["symbol"]]
            if len(matches) > 1:
                self.store.leg_status(row["id"], "Uncertain", "Multiple broker positions match")
                continue
            if matches:
                p = matches[0]
                self.store.leg_status(row["id"], "Open", ticket=p.ticket, volume=p.volume,
                                      position_id=p.identifier, entry_price=p.price_open, stop=p.sl, opened_at=p.time)
                s = json.loads(row["payload"])
                if p.sl <= 0 or p.tp <= 0 or now >= utc(s["closeByUtc"]):
                    self.close(p, now)
                    self.store.leg_status(row["id"], "Uncertain", "Expiry/protection close requested; awaiting broker confirmation")
                continue
            # A position may open and close before the next worker poll.
            s = json.loads(row["payload"])
            deals = self.mt.history_deals_get(utc(s["qualifiedAtUtc"]) - timedelta(minutes=1), now)
            orders = self.mt.orders_get()
            if deals is None or orders is None:
                raise Blocked("Cannot reconcile broker history/orders")
            matching = [d for d in deals if d.magic == row["magic"] and d.symbol == row["symbol"]]
            if any(o.magic == row["magic"] for o in orders):
                self.store.leg_status(row["id"], "Uncertain", "Broker order still pending")
                continue
            position_ids = {d.position_id for d in matching}
            if row["position_id"]:
                position_ids.add(row["position_id"])
            if len(position_ids) != 1:
                self.store.leg_status(row["id"], "Uncertain", "No unique filled broker position; manual review required")
                continue
            history = self.mt.history_deals_get(position=next(iter(position_ids)))
            if history is None:
                raise Blocked("Cannot read broker position history")
            entered = sum(d.volume for d in history if d.entry == self.mt.DEAL_ENTRY_IN)
            exited = sum(d.volume for d in history if d.entry in (self.mt.DEAL_ENTRY_OUT, self.mt.DEAL_ENTRY_OUT_BY))
            if entered > 0 and exited >= entered - 1e-8:
                pnl = sum(d.profit + d.commission + d.swap + d.fee for d in history)
                last = history[-1]
                self.store.leg_status(row["id"], "Closed",
                    f"Confirmed from broker deals; exit reason {last.reason}", pnl=pnl, closed_at=last.time)
            else:
                self.store.leg_status(row["id"], "Uncertain", "Broker closure not confirmed")
        self.store.db.execute("""UPDATE signals SET status='Closed' WHERE status='Tracking'
            AND NOT EXISTS(SELECT 1 FROM legs WHERE signal_id=signals.id
                AND status NOT IN ('Closed','Cancelled'))""")

    def cycle(self, spool, now):
        self.reconcile(now)  # Always track exits, even with entries paused or on Sundays.
        if self.source is not None:
            try:
                self.source.sync(spool, now, validate_intent)
            except FeedError as exc:
                LOG.warning("No new remote entries: %s", exc)
        if self.store.blocked():
            LOG.warning("Uncertain submission: new entries halted; inspect private execution state")
            return
        if self.telegram_execution and (self.source is None or not self.source.is_current(now)):
            return  # Track existing positions, but never act on an offline/stale snapshot.
        pending = [json.loads(row[0]) for row in self.store.db.execute("SELECT payload FROM signals WHERE status IN ('PendingReversal','PendingEntry')")]
        files = sorted(spool.glob("*.json"))
        for item in pending + files:
            try:
                if isinstance(item, Path):
                    if item.stat().st_size > 16384:
                        raise Blocked("Oversized signal file")
                    s = json.loads(item.read_text(encoding="utf-8"))
                    if s.get("flow") not in self.allowed_flows:
                        continue
                    if self.telegram_execution and not self.source.authorizes(s, now):
                        continue
                    if not self.store.remember(s):
                        continue
                else:
                    s = item
                    if self.telegram_execution and not self.source.authorizes(s, now):
                        self.store.signal_status(s["id"], "Rejected", "Signal closed/cancelled or removed from current signed instructions")
                        continue
                if s.get("flow") not in self.allowed_flows:
                    continue
                try:
                    self.execute(s, now)
                except ReversalPending as exc:
                    self.store.signal_status(s["id"], "PendingReversal", str(exc))
                except (Blocked, KeyError, ValueError, TypeError) as exc:
                    active = self.store.db.execute("SELECT 1 FROM legs WHERE signal_id=? AND status IN ('Prepared','Uncertain','Open')",
                                                   (s["id"],)).fetchone()
                    has_legs = self.store.db.execute("SELECT 1 FROM legs WHERE signal_id=?", (s["id"],)).fetchone()
                    wait_for_quote = self.telegram_execution and not has_legs and (
                        "outside the qualified zone" in str(exc) or "Missing or stale broker quote" in str(exc))
                    status = "Tracking" if active else "PendingEntry" if wait_for_quote else "Rejected"
                    self.store.signal_status(s["id"], status, str(exc))
                    LOG.warning("%s %s: %s", status, s["id"], exc)
                self.reconcile(now)
                if self.store.blocked():
                    return
            except (Blocked, KeyError, ValueError, TypeError) as exc:
                LOG.error("Invalid signal item: %s", exc)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--allow-real-orders", action="store_true", help="Owner-only explicit opt-in for the isolated live profile")
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--status", action="store_true", help="Read private database without connecting to MT5")
    parser.add_argument("--check", action="store_true", help="Verify configured connection; never send orders")
    parser.add_argument("--inspect-demo", action="store_true", help="Read demo account and available symbol names; never send orders")
    parser.add_argument("--feed-check", action="store_true", help="Verify the deployed feed without placing or queuing orders")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    config_path = Path(args.config).resolve()
    c = json.loads(config_path.read_text(encoding="utf-8-sig"))
    state = (config_path.parent / c["stateDatabase"]).resolve()
    spool = (config_path.parent / c["spoolDirectory"]).resolve()
    feed_status = state.with_name("feed-status.json")
    worker_status = state.with_name("worker-status.json")
    if args.feed_check:
        source = SignedSignalFeed(c, feed_status)
        data = source.fetch(datetime.now(UTC))
        print(json.dumps({"signatureVerified": True, "generatedAtUtc": data["generatedAtUtc"],
                          "stale": data["stale"], "signalCount": len(data["signals"])}))
        return
    if args.status:
        if not state.exists():
            print("No execution database; no orders have been tracked by this worker.")
            return
        db = sqlite3.connect(f"file:{state.as_posix()}?mode=ro", uri=True)
        db.row_factory = sqlite3.Row
        health = json.loads(worker_status.read_text()) if worker_status.exists() else None
        feed = json.loads(feed_status.read_text()) if feed_status.exists() else None
        ready = bool(health and health.get("state") == "Ready" and
                     0 <= (datetime.now(UTC) - utc(health["checkedAtUtc"])).total_seconds() <= 30)
        if c.get("remoteFeedUrl"):
            ready = bool(ready and feed and feed.get("state") in ("Verified", "WaitingForNextScan")
                         and 0 <= (datetime.now(UTC) - utc(feed["checkedAtUtc"])).total_seconds() <= 60
                         and -30 <= (datetime.now(UTC) - utc(feed["generatedAtUtc"])).total_seconds() <= 1200)
        print(json.dumps({"worker": health, "ready": ready,
                          "entriesEnabled": c.get("entriesEnabled") is True,
                          "entryExecutionPolicy": c.get("entryExecutionPolicy", "legacy"), "feed": feed,
                          "signals": [dict(r) for r in db.execute("SELECT id,status,reason FROM signals")],
                          "legs": [dict(r) for r in db.execute("SELECT * FROM legs")]}, indent=2))
        return
    # Native Windows lock releases on process death; no stale lock recovery guesswork.
    import msvcrt
    import MetaTrader5 as mt5
    if args.check or args.inspect_demo:
        try:
            initialize_terminal(mt5, c)
            a = mt5.account_info()
            expected_mode = mt5.ACCOUNT_TRADE_MODE_DEMO if args.inspect_demo or c.get("mode") != "live" else mt5.ACCOUNT_TRADE_MODE_REAL
            if a is None or a.trade_mode != expected_mode or "exness" not in a.company.lower():
                raise Blocked("Sign into the configured Exness account type in desktop MT5 first")
            if args.inspect_demo:
                symbols = mt5.symbols_get()
                if symbols is None:
                    raise Blocked("Cannot inspect account symbols")
                print(json.dumps({"expectedLogin": a.login, "expectedServer": a.server,
                    "demo": True, "currency": a.currency,
                    "symbols": [s.name for s in symbols if any(x in s.name.upper() for x in
                        ("BTC", "ETH", "XAU", "USOIL", "WTI", "EURUSD", "GBPUSD", "USDJPY", "SOL", "CAKE"))]}, indent=2))
            else:
                state.parent.mkdir(parents=True, exist_ok=True)
                store = Store(state)
                try:
                    Worker(mt5, c, store).account()
                    print("Configured Exness account verified. No orders sent.")
                finally:
                    store.db.close()
        finally:
            mt5.shutdown()
        return
    state.parent.mkdir(parents=True, exist_ok=True)
    spool.mkdir(parents=True, exist_ok=True)
    with open(str(state) + ".lock", "a+b") as lock:
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        store = Store(state)
        source = SignedSignalFeed(c, feed_status) if c.get("remoteFeedUrl") else None
        worker = Worker(mt5, c, store, source=source, allow_real_orders=args.allow_real_orders)
        worker.submission_permission()
        try:
            while True:
                try:
                    if c["mode"] == "live":
                        # Owner can pause entries without interrupting a broker submission.
                        try:
                            current_config = json.loads(config_path.read_text(encoding="utf-8-sig"))
                            c["entriesEnabled"] = current_config.get("entriesEnabled") is True
                        except (OSError, ValueError):
                            c["entriesEnabled"] = False
                    initialize_terminal(mt5, c)
                    worker.cycle(spool, datetime.now(UTC))
                    write_worker_status(worker_status, "Ready")
                except Blocked as exc:
                    write_worker_status(worker_status, "Blocked", str(exc))
                    LOG.warning("Worker blocked: %s", exc)
                except Exception:
                    write_worker_status(worker_status, "Error", "Worker cycle failed; inspect private log")
                    LOG.exception("Worker failed; new submissions stop until next safe reconciliation")
                if args.once:
                    break
                time.sleep(max(1, float(c.get("pollSeconds", 5))))
        finally:
            mt5.shutdown()
            store.db.close()


if __name__ == "__main__":
    main()
