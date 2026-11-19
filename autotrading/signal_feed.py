"""Verify the deployed scanner's signed, Telegram-delivered demo signal feed."""
import base64
from datetime import datetime, timezone
import hashlib
import hmac
import json
import os
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request

MAX_BYTES = 512 * 1024
DOMAIN = b"realtouch-demo-feed-v1\n"
EXPECTED_URL = "https://hilaryrealtouch272.github.io/realtouch-trade/auto-trading-signals.json"


class FeedError(Exception):
    pass


def timestamp(value):
    value = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if value.tzinfo is None:
        raise FeedError("Feed timestamps must include timezone")
    return value.astimezone(timezone.utc)


def verify_feed(body, secret, now):
    if len(body) > MAX_BYTES or len(secret.encode()) < 32:
        raise FeedError("Invalid feed size or signing key")
    try:
        envelope = json.loads(body)
        payload = base64.b64decode(envelope["payload"], validate=True)
        signature = envelope["signature"]
        expected = hmac.new(secret.encode(), DOMAIN + payload, hashlib.sha256).hexdigest()
        if not isinstance(signature, str) or not hmac.compare_digest(signature, expected):
            raise FeedError("Feed signature does not match the deployed scanner")
        data = json.loads(payload)
        if data["version"] != 1 or not isinstance(data["signals"], list) or len(data["signals"]) > 512:
            raise FeedError("Unsupported feed format")
        age = (now - timestamp(data["generatedAtUtc"])).total_seconds()
        if age < -30:
            raise FeedError("Feed timestamp is in the future")
        data["stale"] = age > 300
        return data
    except (KeyError, TypeError, ValueError, AttributeError) as exc:
        raise FeedError("Invalid signed feed") from exc


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class SignedSignalFeed:
    def __init__(self, config, status_path):
        self.url = config.get("remoteFeedUrl", "")
        self.secret = config.get("remoteFeedKey", "")
        if self.url != EXPECTED_URL or len(self.secret.encode()) < 32:
            raise FeedError("Configure the trusted deployed URL and its private signing key")
        self.poll_seconds = max(15, float(config.get("feedPollSeconds", 30)))
        self.next_poll = 0
        self.status_path = Path(status_path)
        self.opener = urllib.request.build_opener(NoRedirect())

    def fetch(self, now):
        request = urllib.request.Request(self.url + "?checked=" + str(int(now.timestamp())),
            headers={"User-Agent": "Realtouch-Demo-Execution/1.0", "Cache-Control": "no-cache"})
        try:
            with self.opener.open(request, timeout=8) as response:
                body = response.read(MAX_BYTES + 1)
        except urllib.error.HTTPError as exc:
            raise FeedError(f"Execution feed returned HTTP {exc.code}") from None
        except (OSError, urllib.error.URLError):
            raise FeedError("Cannot reach the deployed execution feed") from None
        return verify_feed(body, self.secret, now)

    def status(self, **values):
        self.status_path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.status_path.with_suffix(".tmp")
        temp.write_text(json.dumps(values, indent=2), encoding="utf-8")
        os.replace(temp, self.status_path)

    def sync(self, spool, now, validate):
        if time.monotonic() < self.next_poll:
            return
        self.next_poll = time.monotonic() + self.poll_seconds
        try:
            data = self.fetch(now)
            if data["stale"]:
                self.status(state="WaitingForNextScan", checkedAtUtc=now.isoformat(),
                            generatedAtUtc=data["generatedAtUtc"], accepted=0)
                return
            accepted, ignored = 0, 0
            spool.mkdir(parents=True, exist_ok=True)
            for s in data["signals"]:
                try:
                    validate(s, now)
                    if not s.get("notificationId") or not s.get("telegramDeliveredAtUtc"):
                        raise FeedError("Signal lacks confirmed Telegram delivery")
                    delivered = timestamp(s["telegramDeliveredAtUtc"])
                    if delivered > now or delivered < timestamp(s["qualifiedAtUtc"]):
                        raise FeedError("Invalid Telegram delivery timestamp")
                except (Exception,) as exc:
                    # Invalid, expired or Sunday-blocked entries never reach the broker queue.
                    # Do not log the envelope or signing material.
                    ignored += 1
                    continue
                name = hashlib.sha256(s["id"].encode()).hexdigest().upper()
                destination = spool / (name + ".json")
                if destination.exists():
                    continue  # Replays cannot renew deadlines or create duplicate orders.
                temp = spool / (name + ".tmp")
                temp.write_text(json.dumps(s), encoding="utf-8")
                os.replace(temp, destination)
                accepted += 1
            self.status(state="Verified", checkedAtUtc=now.isoformat(), generatedAtUtc=data["generatedAtUtc"],
                        accepted=accepted, ignored=ignored)
        except FeedError as exc:
            self.status(state="Unavailable", checkedAtUtc=now.isoformat(), reason=str(exc))
            raise
