from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace as NS
import unittest

from exness_worker import Blocked, Store, Worker, validate_intent, volume_down

NOW = datetime(2026, 11, 16, 12, tzinfo=timezone.utc)


def signal(flow="vtb", identity="one"):
    return dict(id=flow + ":" + identity, flow=flow, symbol="BTC/USDT", side="Long",
                qualifiedAtUtc=NOW.isoformat(), entryDeadlineUtc=(NOW + timedelta(minutes=5)).isoformat(),
                closeByUtc=(NOW + timedelta(days=20)).isoformat(), entry=100, zoneMin=99, zoneMax=101,
                stop=90, targets=[120] if flow == "vtb" else [110, 120, 130], riskMultiplier=1)


class Broker:
    ACCOUNT_TRADE_MODE_DEMO = 0
    ACCOUNT_MARGIN_MODE_RETAIL_HEDGING = 2
    DEAL_TYPE_BUY, DEAL_TYPE_SELL = 0, 1
    ORDER_TYPE_BUY, ORDER_TYPE_SELL = 0, 1
    ORDER_FILLING_IOC, ORDER_FILLING_FOK, ORDER_FILLING_RETURN = 1, 0, 2
    SYMBOL_TRADE_EXECUTION_MARKET = 2
    TRADE_ACTION_DEAL = 1
    ORDER_TIME_GTC = 0
    TRADE_RETCODE_DONE, TRADE_RETCODE_DONE_PARTIAL = 10009, 10010
    DEAL_ENTRY_IN, DEAL_ENTRY_OUT, DEAL_ENTRY_OUT_BY = 0, 1, 3

    def __init__(self):
        self.a = NS(trade_mode=0, company="Exness", login=123, server="Exness-MT5Trial",
                    margin_mode=2, equity=10000, balance=10000, margin_free=10000,
                    trade_allowed=True, trade_expert=True)
        self.t = NS(connected=True, trade_allowed=True)
        self.info = NS(trade_tick_size=.01, digits=2, point=.01, trade_stops_level=1,
                       volume_min=.01, volume_max=100, volume_step=.01, filling_mode=2, trade_exemode=2)
        self.tick = NS(ask=100.01, bid=100, time=NOW.timestamp())
        self.ps, self.deals, self.sent = [], [], []
        self.response = "fill"

    def account_info(self): return self.a
    def terminal_info(self): return self.t
    def positions_get(self): return self.ps
    def orders_get(self): return []
    def symbol_select(self, *_): return True
    def symbol_info(self, *_): return self.info
    def symbol_info_tick(self, *_): return self.tick
    def order_calc_profit(self, side, symbol, volume, entry, exit):
        return (exit - entry) * volume * (1 if side == 0 else -1)
    def order_calc_margin(self, *_): return 10
    def history_deals_get(self, *args, **kw):
        return [d for d in self.deals if not kw or d.position_id == kw["position"]]
    def order_check(self, *_): return NS(retcode=0)
    def order_send(self, r):
        self.sent.append(r)
        if self.response == "timeout": return None
        if "position" in r:
            self.ps = [p for p in self.ps if p.ticket != r["position"]]
            self.deals.append(NS(position_id=r["position"], magic=r["magic"], symbol=r["symbol"],
                                 volume=r["volume"], entry=1, type=r["type"], profit=5,
                                 commission=-.1, swap=0, fee=0, reason=4, time=int(NOW.timestamp())))
        else:
            ticket = len(self.sent) + 100
            self.ps.append(NS(ticket=ticket, identifier=ticket, magic=r["magic"], symbol=r["symbol"], volume=r["volume"],
                              sl=r["sl"], tp=r["tp"], type=r["type"], price_open=r["price"], profit=0, swap=0,
                              time=int(NOW.timestamp())))
            self.deals.append(NS(position_id=ticket, magic=r["magic"], symbol=r["symbol"],
                                 volume=r["volume"], entry=0, type=r["type"], profit=0,
                                 commission=-.1, swap=0, fee=0, reason=3, time=int(NOW.timestamp())))
        return NS(retcode=10009)


class ExecutionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.mt = Broker()
        self.c = json.loads(Path(__file__).with_name("config.example.json").read_text())
        self.c.update(expectedLogin=123, expectedServer="Exness-MT5Trial", symbols={"BTC/USDT": "BTCUSDm"})
        self.store = Store(self.root / "state.sqlite3")
        self.w = Worker(self.mt, self.c, self.store)

    def tearDown(self):
        self.store.db.close()
        self.temp.cleanup()

    def spool(self, s):
        (self.root / "signal.json").write_text(json.dumps(s))

    def test_demo_fills_once_across_restart(self):
        self.spool(signal())
        self.w.cycle(self.root, NOW)
        restarted = Worker(self.mt, self.c, self.store)
        restarted.cycle(self.root, NOW)
        self.assertEqual(1, len(self.mt.sent))
        self.assertEqual("Open", self.store.db.execute("SELECT status FROM legs").fetchone()[0])

    def test_real_account_is_always_blocked(self):
        self.mt.a.trade_mode = 2
        with self.assertRaisesRegex(Blocked, "DEMO"): self.w.account()
        self.assertEqual([], self.mt.sent)

    def test_account_switch_is_blocked(self):
        self.w.account()
        self.mt.a.login = self.c["expectedLogin"] = 999
        with self.assertRaisesRegex(Blocked, "different account"): self.w.account()

    def test_unconfigured_identity_is_blocked(self):
        self.c["expectedLogin"] = 0
        with self.assertRaisesRegex(Blocked, "login/server"): self.w.account()

    def test_live_mode_not_implemented(self):
        self.c["mode"] = "live"
        with self.assertRaises(Blocked): Worker(self.mt, self.c, self.store)

    def test_stale_and_future_signals_rejected(self):
        for offset in (-3600, 60):
            s = signal()
            s["qualifiedAtUtc"] = (NOW + timedelta(seconds=offset)).isoformat()
            with self.assertRaises(Blocked): validate_intent(s, NOW)

    def test_sunday_uses_lagos_boundary(self):
        sunday = datetime(2026, 11, 14, 23, 30, tzinfo=timezone.utc)
        s = signal()
        s["qualifiedAtUtc"] = sunday.isoformat()
        s["entryDeadlineUtc"] = (sunday + timedelta(minutes=5)).isoformat()
        with self.assertRaisesRegex(Blocked, "Sunday"): validate_intent(s, sunday)

    def test_below_minimum_lot_rejects_no_rounding_up(self):
        self.mt.a.equity = 1
        with self.assertRaisesRegex(Blocked, "lot size"): self.w.plan(signal(), self.mt.a, [], NOW)
        self.assertEqual(0.01, volume_down(.019999, .01))

    def test_main_target_weights_and_original_stops(self):
        requests = self.w.plan(signal("main"), self.mt.a, [], NOW)
        self.assertEqual([110, 120, 130], [r["tp"] for _, r in requests])
        volumes = [r["volume"] for _, r in requests]
        self.assertAlmostEqual(2 * volumes[0], volumes[1], delta=.01)
        self.assertEqual(volumes[0], volumes[2])
        self.assertTrue(all(r["sl"] == 90 for _, r in requests))
        self.assertLessEqual(sum(volumes) * 10.01, 25)

    def test_overlapping_symbol_between_flows_is_blocked(self):
        self.spool(signal())
        self.w.cycle(self.root, NOW)
        with self.assertRaisesRegex(Blocked, "already exists"):
            self.w.plan(signal("main"), self.mt.a, self.mt.ps, NOW)

    def test_drift_spread_and_crossed_stops_reject(self):
        for ask, bid in ((103, 102.99), (100.5, 99), (89, 88.99)):
            self.mt.tick.ask, self.mt.tick.bid = ask, bid
            with self.assertRaises(Blocked): self.w.plan(signal(), self.mt.a, [], NOW)

    def test_missing_mapping_and_stale_quote_reject(self):
        self.c["symbols"] = {}
        with self.assertRaisesRegex(Blocked, "mapping"): self.w.plan(signal(), self.mt.a, [], NOW)
        self.c["symbols"] = {"BTC/USDT": "BTCUSDm"}
        self.mt.tick.time -= 40
        with self.assertRaisesRegex(Blocked, "stale"): self.w.plan(signal(), self.mt.a, [], NOW)

    def test_uncertain_send_never_retried(self):
        self.mt.response = "timeout"
        self.spool(signal())
        self.w.cycle(self.root, NOW)
        self.mt.response = "fill"
        self.w.cycle(self.root, NOW)
        self.assertEqual(1, len(self.mt.sent))
        self.assertTrue(self.store.blocked())

    def test_restart_after_submission_recovers_broker_position(self):
        s = signal()
        self.store.remember(s)
        self.w.execute(s, NOW)
        # Prepared was durably saved before broker send; restart resolves it.
        self.assertTrue(self.store.blocked())
        Worker(self.mt, self.c, self.store).reconcile(NOW)
        self.assertFalse(self.store.blocked())
        self.assertEqual(1, len(self.mt.sent))

    def test_broker_closure_records_actual_pnl(self):
        self.spool(signal())
        self.w.cycle(self.root, NOW)
        self.w.close(self.mt.ps[0], NOW)
        self.w.reconcile(NOW)
        row = self.store.db.execute("SELECT status,pnl FROM legs").fetchone()
        self.assertEqual("Closed", row[0])
        self.assertAlmostEqual(4.8, row[1])

    def test_expiry_still_closes_when_entries_paused(self):
        s = signal()
        s["closeByUtc"] = (NOW + timedelta(seconds=1)).isoformat()
        self.spool(s)
        self.w.cycle(self.root, NOW)
        self.c["entriesEnabled"] = False
        self.mt.tick.time += 2
        self.w.cycle(self.root, NOW + timedelta(seconds=2))
        self.w.reconcile(NOW + timedelta(seconds=2))
        self.assertEqual([], self.mt.ps)
        self.assertEqual("Closed", self.store.db.execute("SELECT status FROM legs").fetchone()[0])

    def test_daily_loss_guard_blocks_new_orders(self):
        self.mt.deals.append(NS(type=0, profit=-100, commission=-1, swap=0, fee=0))
        self.mt.a.balance = 9899
        with self.assertRaisesRegex(Blocked, "Daily account loss"):
            self.w.plan(signal(), self.mt.a, [], NOW)

    def test_unknown_account_exposure_blocks_new_orders(self):
        position = NS(symbol="ETHUSDm", sl=0, profit=0, swap=0)
        with self.assertRaisesRegex(Blocked, "no stop"):
            self.w.plan(signal(), self.mt.a, [position], NOW)

    def test_aggregate_risk_cap_includes_other_positions(self):
        position = NS(symbol="ETHUSDm", sl=90, type=0, volume=4, price_open=100, profit=0, swap=0)
        with self.assertRaisesRegex(Blocked, "Combined account"):
            self.w.plan(signal(), self.mt.a, [position], NOW)

    def test_sunday_reconciles_existing_position(self):
        s = signal()
        self.spool(s)
        self.w.cycle(self.root, NOW)
        sunday = NOW + timedelta(days=6)
        self.mt.tick.time = sunday.timestamp()
        self.w.reconcile(sunday)
        self.assertEqual("Open", self.store.db.execute("SELECT status FROM legs").fetchone()[0])

    def test_wrong_geometry_and_nan_rejected(self):
        for field, value in (("stop", 110), ("entry", float("nan")), ("targets", [80])):
            s = signal()
            s[field] = value
            with self.assertRaises(Blocked): validate_intent(s, NOW)

    def test_all_target_legs_preflight_before_any_submission(self):
        calls = []
        def check(request):
            calls.append(request)
            return NS(retcode=0 if len(calls) < 3 else 10019)
        self.mt.order_check = check
        self.spool(signal("main"))
        self.w.cycle(self.root, NOW)
        self.assertEqual([], self.mt.sent)
        self.assertEqual("Rejected", self.store.db.execute("SELECT status FROM signals").fetchone()[0])

    def test_missing_native_stop_triggers_protection_close(self):
        self.spool(signal())
        self.w.cycle(self.root, NOW)
        self.mt.ps[0].sl = 0
        self.w.reconcile(NOW)
        self.assertEqual([], self.mt.ps)
        self.assertTrue(self.store.blocked())  # Until the close is confirmed in history.
        self.w.reconcile(NOW)
        self.assertFalse(self.store.blocked())

    def test_missing_position_without_history_is_uncertain_not_closed(self):
        self.spool(signal())
        self.w.cycle(self.root, NOW)
        self.mt.ps, self.mt.deals = [], []
        self.w.reconcile(NOW)
        self.assertTrue(self.store.blocked())

    def test_interrupted_unsent_legs_are_cancelled_not_replayed(self):
        s = signal()
        self.store.remember(s)
        self.store.db.execute("INSERT INTO legs(id,signal_id,magic,symbol,status) VALUES('leg',?,123,'BTCUSDm','Planned')", (s["id"],))
        self.w.reconcile(NOW)
        self.assertEqual("Cancelled", self.store.db.execute("SELECT status FROM legs").fetchone()[0])
        self.assertEqual([], self.mt.sent)

    def test_main_submits_three_independent_legs_then_tracks_all(self):
        self.spool(signal("main"))
        self.w.cycle(self.root, NOW)
        self.assertEqual(3, len(self.mt.sent))
        self.assertEqual(["Open"] * 3, [r[0] for r in self.store.db.execute("SELECT status FROM legs")])


if __name__ == "__main__":
    unittest.main()
