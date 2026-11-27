"""Signed feed -> simulated broker -> simulated Telegram. Never connects to MT5."""
import base64
from datetime import timedelta
import hashlib
import hmac
import io
import json
from pathlib import Path
import tempfile
import unittest

from exness_worker import Store, Worker
from execution_reporter import Reporter
from signal_feed import DOMAIN, EXPECTED_URL, SignedSignalFeed
from test_exness_worker import Broker, NOW, signal, NS

KEY = "telegram-execution-unit-test-key-" * 2


class TelegramExecutionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.db = Store(self.root / "execution.sqlite3")
        self.mt = Broker()
        c = json.loads(Path(__file__).with_name("config.example.json").read_text())
        c.update(expectedLogin=123, expectedServer="Exness-MT5Trial", sizingMode="fixed",
                 fixedLots=.01, mainFixedLots=.01, mainExitMode="tp2", entryExecutionPolicy="telegram",
                 symbols={"GBP/USD":"GBPUSDm"}, remoteFeedUrl=EXPECTED_URL, remoteFeedKey=KEY)
        self.source = SignedSignalFeed(c, self.root / "feed-status.json")
        self.worker = Worker(self.mt, c, self.db, self.source)
        self.mt.info.trade_tick_size = .00001
        self.mt.info.point = .00001
        self.mt.info.digits = 5
        self.mt.tick.ask = 1.30948
        self.mt.tick.bid = 1.30938
        self.s = signal("main")
        self.s.update(symbol="GBP/USD", entry=1.30938, zoneMin=1.30926369026, zoneMax=1.30949630974,
                      stop=1.30878, targets=[1.30950, 1.30952, 1.30954],
                      qualifiedAtUtc=(NOW-timedelta(hours=1)).isoformat(),
                      telegramDeliveredAtUtc=(NOW-timedelta(hours=1)+timedelta(seconds=1)).isoformat(),
                      notificationId="telegram-entry", entryPolicy="telegram", contractVersion=2,
                      closeByUtc=(NOW+timedelta(days=1)).isoformat(),
                      entryDeadlineUtc=(NOW+timedelta(days=1)).isoformat())

    def tearDown(self):
        self.db.db.close()
        self.temp.cleanup()

    def publish(self, signals, now=NOW):
        payload=json.dumps(dict(version=1, generatedAtUtc=now.isoformat(), signals=[], telegramSignals=signals)).encode()
        body=json.dumps(dict(payload=base64.b64encode(payload).decode(),
                             signature=hmac.new(KEY.encode(),DOMAIN+payload,hashlib.sha256).hexdigest())).encode()
        self.source.opener.open=lambda *a, **k: io.BytesIO(body)
        self.source.next_poll=0

    def test_signed_telegram_entry_fills_once_without_extra_filters_and_reports_broker_fill(self):
        self.publish([self.s])
        self.worker.cycle(self.root/"spool",NOW)
        self.worker.cycle(self.root/"spool",NOW)
        self.assertEqual(1,len(self.mt.sent))
        order=self.mt.sent[0]
        self.assertEqual((.01,1.30878,1.30952),tuple(order[k] for k in ("volume","sl","tp")))
        self.assertGreater(self.mt.tick.ask-self.mt.tick.bid, .1*abs(self.s['entry']-self.s['stop']))
        self.assertLess((order['tp']-order['price'])/(order['price']-order['sl']),.8)
        sent=[]
        def send(text):
            sent.append(text)
            return dict(ok=True,result=dict(message_id=len(sent),chat=dict(id=-5446001871)))
        reporter=Reporter(self.root/"notices.sqlite3",-5446001871,send,NOW.timestamp()-7200,
                          execution_mode="demo",main_exit_mode="tp2")
        rows=self.db.db.execute("select legs.*,signals.payload from legs join signals on legs.signal_id=signals.id").fetchall()
        reporter.report(rows);reporter.report(rows);reporter.db.close()
        self.assertEqual(1,len(sent));self.assertIn('BOT EXECUTION',sent[0]);self.assertIn('TP2',sent[0])

    def test_outside_zone_waits_then_fills_when_price_returns_without_entry_timer(self):
        self.mt.tick.ask=1.3097;self.mt.tick.bid=1.3096
        self.publish([self.s]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual([],self.mt.sent)
        self.assertEqual('PendingEntry',self.db.db.execute('select status from signals').fetchone()[0])
        later=NOW+timedelta(minutes=30)
        self.mt.tick.ask=1.30948;self.mt.tick.bid=1.30938;self.mt.tick.time=later.timestamp()
        self.publish([self.s],later);self.worker.cycle(self.root/'spool',later)
        self.assertEqual(1,len(self.mt.sent))

    def test_closed_instruction_cancels_pending_entry_even_with_spool_file_present(self):
        self.mt.tick.ask=1.3097;self.mt.tick.bid=1.3096
        self.publish([self.s]);self.worker.cycle(self.root/'spool',NOW)
        self.mt.tick.ask=1.30948;self.mt.tick.bid=1.30938
        self.publish([]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual([],self.mt.sent)
        self.assertEqual('Rejected',self.db.db.execute('select status from signals').fetchone()[0])

    def test_old_ledger_or_unannounced_signal_is_not_imported(self):
        old=dict(self.s);old.pop('entryPolicy');old.pop('contractVersion')
        unannounced=dict(self.s);unannounced.pop('telegramDeliveredAtUtc')
        self.publish([old,unannounced]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual([],self.mt.sent)

    def test_unavailable_or_outdated_snapshot_cannot_authorize_an_order(self):
        self.publish([self.s],NOW-timedelta(minutes=21))
        self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual([],self.mt.sent)
        self.assertFalse(self.source.is_current(NOW))

    def test_signed_snapshot_remains_current_between_regular_fifteen_minute_scans(self):
        self.publish([self.s],NOW-timedelta(minutes=12))
        self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual(1,len(self.mt.sent))

    def test_vtb_telegram_instruction_has_no_extra_drift_spread_or_reward_filter(self):
        self.worker.c['symbols']['BTC/USDT']='BTCUSDm'
        self.mt.info.trade_tick_size=.01;self.mt.info.point=.01;self.mt.info.digits=2
        self.mt.tick.ask=119;self.mt.tick.bid=117
        s=dict(self.s);s.update(id='vtb:new',flow='vtb',symbol='BTC/USDT',entry=100,
                               zoneMin=100,zoneMax=100,stop=90,targets=[120])
        self.publish([s]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual(1,len(self.mt.sent))
        self.assertEqual((.01,90,120),tuple(self.mt.sent[0][k] for k in ('volume','sl','tp')))

    def test_local_payload_cannot_override_current_signed_instruction(self):
        self.publish([self.s]);self.source.sync(self.root/'spool',NOW,__import__('exness_worker').validate_intent)
        p=next((self.root/'spool').glob('*.json'))
        changed=dict(self.s);changed['stop']=1.30;p.write_text(json.dumps(changed))
        self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual([],self.mt.sent)

    def test_other_pair_pending_order_does_not_block_this_signal(self):
        self.mt.orders_get=lambda:[NS(symbol='EURUSDm')]
        self.publish([self.s]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual(1,len(self.mt.sent))

    def test_removed_filters_do_not_require_unused_threshold_configuration(self):
        for key in ('maxEntryDriftR','maxSpreadR','minimumRewardRisk'):self.worker.c.pop(key)
        self.worker=Worker(self.mt,self.worker.c,self.db,self.source)
        self.publish([self.s]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual(1,len(self.mt.sent))

    def test_same_pair_pending_order_still_prevents_duplicate_exposure(self):
        self.mt.orders_get=lambda:[NS(symbol='GBPUSDm')]
        self.publish([self.s]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual([],self.mt.sent)

    def test_telegram_policy_uses_actual_free_margin_without_extra_ten_percent_reserve(self):
        self.mt.a.margin_free=10.5  # Simulated required margin is 10.
        self.publish([self.s]);self.worker.cycle(self.root/'spool',NOW)
        self.assertEqual(1,len(self.mt.sent))


if __name__ == '__main__':
    unittest.main()
