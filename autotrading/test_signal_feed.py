import base64
from datetime import timedelta
import hashlib
import hmac
import json
from pathlib import Path
import tempfile
import unittest
from signal_feed import DOMAIN, EXPECTED_URL, FeedError, SignedSignalFeed, verify_feed
from test_exness_worker import NOW, signal
from exness_worker import validate_intent

KEY = 'unit-test-key-' * 4

def envelope(signals, at=NOW):
    payload = json.dumps(dict(version=1, generatedAtUtc=at.isoformat(), signals=signals)).encode()
    return json.dumps(dict(payload=base64.b64encode(payload).decode(),
        signature=hmac.new(KEY.encode(), DOMAIN+payload, hashlib.sha256).hexdigest())).encode()

class FeedTests(unittest.TestCase):
    def test_signature_tampering_and_wrong_key_are_rejected(self):
        body = envelope([])
        self.assertFalse(verify_feed(body, KEY, NOW)['stale'])
        for bad in (KEY+'x', 'x'*40):
            with self.assertRaises(FeedError): verify_feed(body, bad, NOW)
        data=json.loads(body)
        data['payload']=base64.b64encode(b'{}').decode()
        with self.assertRaises(FeedError): verify_feed(json.dumps(data).encode(),KEY,NOW)

    def test_old_signed_feed_cannot_authorize_new_entries(self):
        self.assertTrue(verify_feed(envelope([],NOW-timedelta(minutes=6)),KEY,NOW)['stale'])
        with self.assertRaises(FeedError): verify_feed(envelope([],NOW+timedelta(minutes=2)),KEY,NOW)

    def test_only_fresh_telegram_delivered_entries_reach_spool(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            feed=SignedSignalFeed(dict(remoteFeedUrl=EXPECTED_URL,remoteFeedKey=KEY),root/'status.json')
            approved=signal()
            approved.update(notificationId='entry',telegramDeliveredAtUtc=NOW.isoformat())
            unannounced=signal(identity='unannounced')
            stale=signal(identity='old')
            stale['qualifiedAtUtc']=(NOW-timedelta(hours=1)).isoformat()
            feed.fetch=lambda now:verify_feed(envelope([approved,unannounced,stale]),KEY,now)
            feed.sync(root/'spool',NOW,validate_intent)
            files=list((root/'spool').glob('*.json'))
            self.assertEqual(1,len(files))
            self.assertEqual(approved,json.loads(files[0].read_text()))
            feed.next_poll=0
            feed.sync(root/'spool',NOW,validate_intent)
            self.assertEqual(1,len(list((root/'spool').glob('*.json'))))

    def test_untrusted_url_is_rejected(self):
        with self.assertRaises(FeedError):
            SignedSignalFeed(dict(remoteFeedUrl='http://example.com',remoteFeedKey=KEY),'unused')

if __name__=='__main__': unittest.main()
