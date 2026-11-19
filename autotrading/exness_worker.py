"""Private local signal spool -> Exness MT5 DEMO execution. No live mode.

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
    if qualified > now or now > deadline or now - qualified > max_age:
        raise Blocked("Stale, expired or future signal; never enter historical alerts")
    if deadline < qualified or deadline - qualified > max_age or close_by <= now:
        raise Blocked("Invalid signal lifetime")
    if (now + timedelta(hours=1)).weekday() == 6:
        raise Blocked("Sunday in Lagos: existing positions only")
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
    def __init__(self, mt5, config, store, source=None):
        self.mt = mt5
        self.c = config
        self.store = store
        self.source = source
        if config.get("mode") != "demo":
            raise Blocked("Only demo execution is implemented; live accounts are prohibited")
        for key, ceiling in (("riskPercent", 0.25), ("maxOpenRiskPercent", 0.5),
                             ("dailyLossPercent", 1), ("maxEntryDriftR", 0.1), ("maxSpreadR", 0.1)):
            if positive(config[key]) > ceiling:
                raise Blocked(f"{key} exceeds this demo implementation's limit")
        if not 0.8 <= positive(config["minimumRewardRisk"]) <= 10:
            raise Blocked("Invalid minimum reward/risk")
        if not 0 <= int(config["deviationPoints"]) <= 20:
            raise Blocked("Invalid deviation allowance")

    def account(self):
        a, terminal = self.mt.account_info(), self.mt.terminal_info()
        if a is None or terminal is None or not terminal.connected:
            raise Blocked("MT5 is disconnected")
        if a.trade_mode != self.mt.ACCOUNT_TRADE_MODE_DEMO or "exness" not in a.company.lower():
            raise Blocked("Connected account must be an Exness DEMO")
        if (not self.c.get("expectedLogin") or not self.c.get("expectedServer") or
                a.login != int(self.c["expectedLogin"]) or a.server != self.c["expectedServer"]):
            raise Blocked("Demo login/server do not match the configured account")
        if a.margin_mode != self.mt.ACCOUNT_MARGIN_MODE_RETAIL_HEDGING:
            raise Blocked("A hedging demo account is required for independent target legs")
        binding = f"{a.login}@{a.server}"
        row = self.store.db.execute("SELECT value FROM meta WHERE key='account'").fetchone()
        if row and row[0] != binding:
            raise Blocked("Execution database belongs to a different account")
        self.store.db.execute("INSERT OR IGNORE INTO meta VALUES('account',?)", (binding,))
        return a, terminal

    def positions(self):
        positions = self.mt.positions_get()
        if positions is None:
            raise Blocked("Cannot read broker positions")
        return positions

    def quote(self, symbol, now):
        if not self.mt.symbol_select(symbol, True):
            raise Blocked("Configured broker symbol is unavailable")
        info, tick = self.mt.symbol_info(symbol), self.mt.symbol_info_tick(symbol)
        if info is None or tick is None or abs(now.timestamp() - tick.time) > 30:
            raise Blocked("Missing or stale broker quote")
        if positive(tick.ask) < positive(tick.bid):
            raise Blocked("Invalid broker spread")
        return info, tick

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
        symbol = self.c["symbols"].get(s["symbol"])
        if not symbol:
            raise Blocked("No explicit Exness symbol mapping; never guess suffixes")
        if any(p.symbol == symbol for p in positions):
            raise Blocked("A position already exists in this symbol, across either flow")
        orders = self.mt.orders_get()
        if orders is None or orders:
            raise Blocked("Cannot verify pending orders or account has pending orders")
        self.daily_guard(a, positions, now)
        info, tick = self.quote(symbol, now)
        long = s["side"] == "Long"
        direction = 1 if long else -1
        action = self.mt.ORDER_TYPE_BUY if long else self.mt.ORDER_TYPE_SELL
        price = tick.ask if long else tick.bid
        reference_risk = abs(s["entry"] - s["stop"])
        if abs(price - s["entry"]) > reference_risk * self.c["maxEntryDriftR"]:
            raise Blocked("Broker entry has drifted too far from the qualified entry")
        if s["flow"] == "main" and not s["zoneMin"] <= price <= s["zoneMax"]:
            raise Blocked("Broker entry is outside the qualified zone")
        if tick.ask - tick.bid > reference_risk * self.c["maxSpreadR"]:
            raise Blocked("Broker spread exceeds the limit")
        grid = positive(info.trade_tick_size)
        stop = round(round(s["stop"] / grid) * grid, info.digits)
        targets = [round(round(t / grid) * grid, info.digits) for t in s["targets"]]
        # Native stops must be valid against the closing side of the spread too.
        closing_price = tick.bid if long else tick.ask
        distance = max(info.trade_stops_level * info.point, grid)
        if direction * (closing_price - stop) < distance or any(
                direction * (t - closing_price) < distance for t in targets):
            raise Blocked("Stop or target already crossed / too close for the broker")
        if min(direction * (t - price) for t in targets) / abs(price - stop) < self.c["minimumRewardRisk"]:
            raise Blocked("Insufficient remaining reward/risk")
        positive(a.equity)
        budget = a.equity * self.c["riskPercent"] / 100 * s["riskMultiplier"] * 0.90
        loss = self.mt.order_calc_profit(action, symbol, 1.0, price, stop)
        if loss is None or not math.isfinite(loss) or loss >= 0:
            raise Blocked("Cannot calculate stop loss in account currency")
        weights = (0.25, 0.50, 0.25) if s["flow"] == "main" else (1.0,)
        volumes = [volume_down(budget * w / abs(loss), positive(info.volume_step)) for w in weights]
        if any(v < positive(info.volume_min) or v > positive(info.volume_max) for v in volumes):
            raise Blocked("Broker minimum/maximum lot size cannot meet the risk budget")
        planned = sum(volumes) * abs(loss)
        if self.risk(positions) + planned > a.equity * self.c["maxOpenRiskPercent"] / 100:
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
        if any(m is None or not math.isfinite(m) or m < 0 for m in margins) or sum(margins) > a.margin_free * 0.9:
            raise Blocked("Insufficient free margin")
        for _, request in requests:
            checked = self.mt.order_check(request)
            if checked is None or checked.retcode != 0:
                raise Blocked("Broker order preflight rejected a target leg")
        return requests

    def execute(self, s, now):
        started = time.monotonic()
        a, terminal = self.account()
        if not self.c.get("entriesEnabled"):
            raise Blocked("New demo entries are paused")
        if self.store.blocked():
            raise Blocked("Unresolved broker submission; inspect before any new entry")
        if not terminal.trade_allowed or not a.trade_allowed or not a.trade_expert:
            raise Blocked("MT5 algorithmic trading is disabled")
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
            # Check each leg against a fresh executable quote. Preflight estimates
            # do not authorize chasing a price during a slow broker submission.
            info, tick = self.quote(request["symbol"], send_now)
            current = tick.ask if s["side"] == "Long" else tick.bid
            if (abs(current - s["entry"]) > abs(s["entry"] - s["stop"]) * self.c["maxEntryDriftR"]
                    or tick.ask - tick.bid > abs(s["entry"] - s["stop"]) * self.c["maxSpreadR"]
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
            request["volume"] = min(request["volume"], volume_down(leg_budget / abs(per_lot), info.volume_step))
            if (request["volume"] < info.volume_min or self.risk(positions) + request["volume"] * abs(per_lot)
                    > a.equity * self.c["maxOpenRiskPercent"] / 100):
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

    def close(self, p, now):
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
        for path in sorted(spool.glob("*.json")):
            try:
                if path.stat().st_size > 16384:
                    raise Blocked("Oversized signal file")
                s = json.loads(path.read_text(encoding="utf-8"))
                if not self.store.remember(s):
                    continue
                try:
                    self.execute(s, now)
                except (Blocked, KeyError, ValueError, TypeError) as exc:
                    active = self.store.db.execute("SELECT 1 FROM legs WHERE signal_id=? AND status IN ('Prepared','Uncertain','Open')",
                                                   (s["id"],)).fetchone()
                    self.store.signal_status(s["id"], "Tracking" if active else "Rejected", str(exc))
                    LOG.warning("Rejected %s: %s", s["id"], exc)
                self.reconcile(now)
                if self.store.blocked():
                    return
            except (Blocked, KeyError, ValueError, TypeError) as exc:
                LOG.error("Invalid spool file %s: %s", path.name, exc)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--status", action="store_true", help="Read private database without connecting to MT5")
    parser.add_argument("--check", action="store_true", help="Verify configured demo connection; never send orders")
    parser.add_argument("--inspect-demo", action="store_true", help="Read demo account and available symbol names; never send orders")
    parser.add_argument("--feed-check", action="store_true", help="Verify the deployed feed without placing or queuing orders")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    config_path = Path(args.config).resolve()
    c = json.loads(config_path.read_text(encoding="utf-8-sig"))
    state = (config_path.parent / c["stateDatabase"]).resolve()
    spool = (config_path.parent / c["spoolDirectory"]).resolve()
    feed_status = state.with_name("feed-status.json")
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
        print(json.dumps({"feed": json.loads(feed_status.read_text()) if feed_status.exists() else None,
                          "signals": [dict(r) for r in db.execute("SELECT id,status,reason FROM signals")],
                          "legs": [dict(r) for r in db.execute("SELECT * FROM legs")]}, indent=2))
        return
    # Native Windows lock releases on process death; no stale lock recovery guesswork.
    import msvcrt
    import MetaTrader5 as mt5
    if args.check or args.inspect_demo:
        try:
            connected = mt5.initialize(c["terminalPath"]) if c.get("terminalPath") else mt5.initialize()
            if not connected:
                raise Blocked("Start desktop MT5 and sign into your Exness demo")
            a = mt5.account_info()
            if a is None or a.trade_mode != mt5.ACCOUNT_TRADE_MODE_DEMO or "exness" not in a.company.lower():
                raise Blocked("Sign into an Exness DEMO account in desktop MT5 first")
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
                    print("Exness demo account verified. No orders sent.")
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
        worker = Worker(mt5, c, store, source=source)
        try:
            while True:
                try:
                    connected = mt5.initialize(c["terminalPath"]) if c.get("terminalPath") else mt5.initialize()
                    if not connected:
                        raise Blocked("Start desktop MT5 and sign into the configured Exness demo")
                    worker.cycle(spool, datetime.now(UTC))
                except Blocked as exc:
                    LOG.warning("Worker blocked: %s", exc)
                except Exception:
                    LOG.exception("Worker failed; new submissions stop until next safe reconciliation")
                if args.once:
                    break
                time.sleep(max(1, float(c.get("pollSeconds", 5))))
        finally:
            mt5.shutdown()
            store.db.close()


if __name__ == "__main__":
    main()
