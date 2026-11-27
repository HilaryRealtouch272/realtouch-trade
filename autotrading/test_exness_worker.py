from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace as NS
import unittest
from unittest.mock import patch

from exness_worker import Blocked, Store, Worker, validate_intent, volume_down, initialize_terminal

NOW = datetime(2026, 11, 16, 12, tzinfo=timezone.utc)


def signal(flow="vtb", identity="one"):
    return dict(id=flow + ":" + identity, flow=flow, symbol="BTC/USDT", side="Long",
                qualifiedAtUtc=NOW.isoformat(), entryDeadlineUtc=(NOW + timedelta(minutes=5)).isoformat(),
                closeByUtc=(NOW + timedelta(days=20)).isoformat(), entry=100, zoneMin=99, zoneMax=101,
                stop=90, targets=[120] if flow == "vtb" else [110, 120, 130], riskMultiplier=1)


class Broker:
    ACCOUNT_TRADE_MODE_DEMO = 0
    ACCOUNT_TRADE_MODE_REAL = 2
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

    def test_fixed_vtb_demo_uses_exact_lots_without_cash_caps(self):
        self.c.update(sizingMode="fixed",fixedLots=.01)
        self.mt.a.equity=20
        requests=self.w.plan(signal(),self.mt.a,[],NOW)
        self.assertEqual(.01,requests[0][1]['volume'])
        self.assertEqual(90,requests[0][1]['sl'])
        self.assertEqual(120,requests[0][1]['tp'])
        with self.assertRaisesRegex(Blocked,'target/partial'):
            self.w.plan(signal('main'),self.mt.a,[],NOW)

    def test_fixed_main_two_targets_fills_once_and_preserves_stop(self):
        self.c.update(sizingMode="fixed", fixedLots=.01, mainFixedLots=.02, mainExitMode="tp1_tp2")
        self.spool(signal("main"))
        self.w.cycle(self.root, NOW)
        Worker(self.mt, self.c, self.store).cycle(self.root, NOW)
        self.assertEqual([.01, .01], [r["volume"] for r in self.mt.sent])
        self.assertEqual([110, 120], [r["tp"] for r in self.mt.sent])
        self.assertTrue(all(r["sl"] == 90 for r in self.mt.sent))
        self.assertEqual(2, self.store.db.execute("select count(*) from legs where status='Open'").fetchone()[0])

    def test_fixed_main_rejects_unsplittable_volume_and_keeps_vtb_single(self):
        self.c.update(sizingMode="fixed", fixedLots=.01, mainFixedLots=.02, mainExitMode="tp1_tp2")
        self.mt.info.volume_min=.1
        with self.assertRaises(Blocked): self.w.plan(signal("main"),self.mt.a,[],NOW)
        self.mt.info.volume_min=.01
        requests=self.w.plan(signal(),self.mt.a,[],NOW)
        self.assertEqual(1,len(requests))
        self.assertEqual(.01,requests[0][1]["volume"])
        self.assertEqual(120,requests[0][1]["tp"])

    def test_account_strategy_assignment_filters_before_submission(self):
        self.c["allowedFlows"]=["vtb"]
        self.w=Worker(self.mt,self.c,self.store)
        with self.assertRaisesRegex(Blocked,"not assigned"):
            self.w.plan(signal("main"),self.mt.a,[],NOW)
        self.spool(signal("main"));self.w.cycle(self.root,NOW)
        self.assertEqual([],self.mt.sent)
        self.assertEqual(0,self.store.db.execute("select count(*) from signals").fetchone()[0])
        self.spool(signal());self.w.cycle(self.root,NOW)
        self.assertEqual(1,len(self.mt.sent))

    def test_strategy_assignment_does_not_stop_existing_trade_expiry(self):
        s=signal();s["closeByUtc"]=(NOW+timedelta(seconds=1)).isoformat()
        self.spool(s);self.w.cycle(self.root,NOW)
        self.c["allowedFlows"]=["main"]
        w=Worker(self.mt,self.c,self.store);self.mt.tick.time+=2
        w.reconcile(NOW+timedelta(seconds=2));w.reconcile(NOW+timedelta(seconds=2))
        self.assertEqual([],self.mt.ps)
        self.assertEqual("Closed",self.store.db.execute("select status from legs").fetchone()[0])

    def test_invalid_strategy_assignment_is_rejected(self):
        for flows in ([],["other"],"vtb"):
            self.c["allowedFlows"]=flows
            with self.assertRaisesRegex(Blocked,"allowedFlows"):
                Worker(self.mt,self.c,self.store)

    def test_saturday_blocks_noncrypto_but_not_crypto(self):
        saturday=NOW+timedelta(days=5)
        for flow in ("main","vtb"):
            s=signal(flow);s.update(qualifiedAtUtc=saturday.isoformat(),entryDeadlineUtc=(saturday+timedelta(minutes=5)).isoformat())
            validate_intent(s,saturday)
            s["symbol"]="XAU/USD"
            with self.assertRaisesRegex(Blocked,"Non-crypto"):
                validate_intent(s,saturday)

    def test_real_account_is_always_blocked(self):
        self.mt.a.trade_mode = 2
        with self.assertRaisesRegex(Blocked, "account type"): self.w.account()
        self.assertEqual([], self.mt.sent)

    def test_account_switch_is_blocked(self):
        self.w.account()
        self.mt.a.login = self.c["expectedLogin"] = 999
        with self.assertRaisesRegex(Blocked, "different account"): self.w.account()

    def test_unconfigured_identity_is_blocked(self):
        self.c["expectedLogin"] = 0
        with self.assertRaisesRegex(Blocked, "login/server"): self.w.account()

    def test_live_mode_requires_exact_profile(self):
        self.c["mode"] = "live"
        with self.assertRaises(Blocked): Worker(self.mt, self.c, self.store)

    def live_worker(self, enabled=False, opted_in=False):
        self.c.update(mode="live", sizingMode="fixed", fixedLots=.01, mainFixedLots=.01,
                      mainExitMode="tp2", entriesEnabled=enabled)
        self.mt.a.trade_mode = self.mt.ACCOUNT_TRADE_MODE_REAL
        return Worker(self.mt, self.c, self.store, allow_real_orders=opted_in)

    def test_live_requires_manual_launch_opt_in_and_enabled_configuration(self):
        w = self.live_worker(enabled=True)
        with self.assertRaisesRegex(Blocked, "allow-real-orders"): w.execute(signal(), NOW)
        self.assertEqual([], self.mt.sent)
        w = self.live_worker(opted_in=True)
        with self.assertRaisesRegex(Blocked, "paused"): w.execute(signal(), NOW)
        self.assertEqual([], self.mt.sent)

    def test_live_profile_submits_one_main_tp2_leg_once(self):
        w = self.live_worker(enabled=True, opted_in=True)
        self.spool(signal("main")); w.cycle(self.root, NOW)
        w.cycle(self.root, NOW)
        self.assertEqual(1, len(self.mt.sent))
        self.assertEqual((.01, 90, 120), tuple(self.mt.sent[0][k] for k in ("volume", "sl", "tp")))

    def test_tp2_does_not_reject_already_crossed_unselected_tp1(self):
        w = self.live_worker()
        s = signal("main"); s["targets"][0] = 100.005
        requests = w.plan(s, self.mt.a, [], NOW)
        self.assertEqual(120, requests[0][1]["tp"])

    def test_live_profile_vtb_single_target_and_no_wti_even_if_mapped(self):
        w = self.live_worker()
        requests = w.plan(signal(), self.mt.a, [], NOW)
        self.assertEqual(1, len(requests))
        self.assertEqual(.01, requests[0][1]["volume"])
        self.c["symbols"]["WTI/USD"] = "USOILm"
        s = signal(); s["symbol"] = "WTI/USD"
        with self.assertRaisesRegex(Blocked, "WTI is excluded"): w.plan(s, self.mt.a, [], NOW)
        self.c["symbols"]["BTC/USDT"] = "USOILm"
        with self.assertRaisesRegex(Blocked, "WTI is excluded"): w.plan(signal(), self.mt.a, [], NOW)

    def test_live_profile_rejects_demo_and_does_not_reuse_demo_database(self):
        self.w.account()
        w = self.live_worker()
        self.mt.a.trade_mode = 0
        with self.assertRaises(Blocked): w.account()
        self.mt.a.trade_mode = 2
        self.c["expectedLogin"] = self.mt.a.login = 999
        with self.assertRaisesRegex(Blocked, "different account"): w.account()

    def test_live_broker_minimum_never_rounds_up(self):
        w = self.live_worker()
        self.mt.info.volume_min = .1
        with self.assertRaisesRegex(Blocked, "minimum/maximum"): w.plan(signal(), self.mt.a, [], NOW)

    def test_quote_waits_for_new_subscription_without_loosening_freshness(self):
        fresh = self.mt.tick
        with patch.object(self.mt, "symbol_info_tick", side_effect=[None, fresh]), patch("exness_worker.time.sleep") as pause:
            self.assertEqual(fresh, self.w.quote("BTCUSDm", NOW)[1])
            pause.assert_called_once_with(.25)
        self.mt.tick = NS(ask=100.01, bid=100, time=NOW.timestamp()-60)
        with patch("exness_worker.time.sleep") as pause:
            with self.assertRaisesRegex(Blocked, "stale broker quote"): self.w.quote("BTCUSDm", NOW)
            self.assertEqual(12, pause.call_count)

    def test_live_terminal_connection_uses_portable_mode_and_reports_failure_code(self):
        from unittest.mock import Mock
        mt = Mock()
        mt.initialize.return_value = True
        initialize_terminal(mt, {"mode":"live", "terminalPath":"isolated.exe"})
        mt.initialize.assert_called_once_with("isolated.exe", portable=True)
        mt.initialize.return_value = False
        mt.last_error.return_value = (-10004, "IPC unavailable")
        with self.assertRaisesRegex(Blocked, "code -10004"):
            initialize_terminal(mt, {"mode":"live", "terminalPath":"isolated.exe"})
        mt.shutdown.assert_called_once()

    def opposite(self, flow="vtb", references=None):
        s = signal(flow, "opposite")
        s.update(side="Short", stop=110, targets=[80] if flow == "vtb" else [90, 80, 70])
        if references is not None: s["replaces"] = references
        return s

    def test_vtb_reversal_waits_for_broker_close_then_opens_once(self):
        self.spool(signal()); self.w.cycle(self.root, NOW)
        self.spool(self.opposite(references=["vtb:one"]))
        self.w.cycle(self.root, NOW)
        self.assertEqual(2, len(self.mt.sent))  # entry + ticket-scoped close only
        self.assertIn("position", self.mt.sent[-1])
        self.assertEqual("PendingReversal", self.store.db.execute("select status from signals where id='vtb:opposite'").fetchone()[0])
        self.w.cycle(self.root, NOW)
        self.w.cycle(self.root, NOW)
        self.assertEqual(3, len(self.mt.sent))
        self.assertEqual(self.mt.ORDER_TYPE_SELL, self.mt.sent[-1]["type"])
        self.assertEqual(1, len(self.mt.ps))

    def test_reversal_never_closes_main_position_or_untracked_position(self):
        self.spool(signal("main")); self.w.cycle(self.root, NOW)
        before = len(self.mt.sent)
        self.spool(self.opposite(references=["main:one"]))
        self.w.cycle(self.root, NOW)
        self.assertEqual(before, len(self.mt.sent))

    def test_reversal_requires_same_symbol_and_opposite_direction(self):
        self.spool(signal()); self.w.cycle(self.root, NOW)
        s = signal(identity="same"); s["replaces"] = ["vtb:one"]
        self.spool(s); self.w.cycle(self.root, NOW)
        self.assertEqual(1, len(self.mt.sent))

    def test_ambiguous_reversal_close_is_not_resent_or_hedged(self):
        self.spool(signal()); self.w.cycle(self.root, NOW)
        self.mt.response = "timeout"
        self.spool(self.opposite(references=["vtb:one"]))
        self.w.cycle(self.root, NOW)
        self.w.cycle(self.root, NOW)
        self.w.cycle(self.root, NOW)
        self.assertEqual(2, len(self.mt.sent))
        self.assertEqual(1, len(self.mt.ps))

    def test_stale_reversal_never_closes_existing_position(self):
        self.spool(signal()); self.w.cycle(self.root, NOW)
        self.spool(self.opposite(references=["vtb:one"]))
        self.w.cycle(self.root, NOW + timedelta(hours=1))
        self.assertEqual(1, len(self.mt.sent))

    def gbp_zone_worker(self):
        self.c.update(sizingMode="fixed", fixedLots=.01, mainFixedLots=.01,
                      mainExitMode="tp2", mainEntryPolicy="zone", symbols={"GBP/USD":"GBPUSDm"})
        self.w = Worker(self.mt, self.c, self.store)
        self.mt.info.trade_tick_size=.00001; self.mt.info.point=.00001; self.mt.info.digits=5
        self.mt.tick.ask=1.30948; self.mt.tick.bid=1.30947
        s=signal("main"); s.update(symbol="GBP/USD",entry=1.30938,zoneMin=1.30926369026,
            zoneMax=1.30949630974,stop=1.30878,targets=[1.30998,1.3109,1.31242])
        return s

    def test_main_zone_accepts_in_range_gbp_beyond_extra_drift_cap(self):
        s=self.gbp_zone_worker()
        self.assertGreater(abs(self.mt.tick.ask-s['entry']),abs(s['entry']-s['stop'])*.1)
        self.spool(s); self.w.cycle(self.root,NOW)
        self.assertEqual(1,len(self.mt.sent))
        self.assertEqual((.01,1.30878,1.3109),tuple(self.mt.sent[0][k] for k in ('volume','sl','tp')))

    def test_main_zone_rechecks_actual_quote_before_submit(self):
        s=self.gbp_zone_worker()
        original=self.mt.order_check; calls=0
        def check(request):
            nonlocal calls
            calls+=1
            result=original(request)
            if calls==1: self.mt.tick.ask=1.3096; self.mt.tick.bid=1.30959
            return result
        self.mt.order_check=check
        self.spool(s);self.w.cycle(self.root,NOW)
        self.assertEqual([],self.mt.sent)

    def test_main_zone_does_not_accept_outside_range_or_stale_signal(self):
        s=self.gbp_zone_worker();self.mt.tick.ask=1.3096;self.mt.tick.bid=1.30959
        with self.assertRaisesRegex(Blocked,'outside the qualified zone'):self.w.plan(s,self.mt.a,[],NOW)
        self.mt.tick.ask=1.30948;self.mt.tick.bid=1.30947
        with self.assertRaisesRegex(Blocked,'Stale'):self.w.plan(s,self.mt.a,[],NOW+timedelta(hours=1))

    def test_main_zone_policy_does_not_change_vtb_drift_guard(self):
        self.c['mainEntryPolicy']='zone';self.w=Worker(self.mt,self.c,self.store)
        self.assertTrue(self.w.entry_drift_exceeded(signal(),102))

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
