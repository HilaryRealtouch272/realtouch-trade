"""Read-only AWS market-data bridge. Never imports order execution code."""
import json
import math
import time
import urllib.request
from datetime import datetime, timezone, timedelta
from pathlib import Path

UTC = timezone.utc
BUCKET = 'realtouch-demo-bootstrap-271251458596'
KEY = 'market-data/vtb-snapshot.json'

def stamp(d):
    return d.isoformat().replace('+00:00', 'Z')

def daily_from_hourly(rows, now):
    groups = {}
    seen = set()
    for r in rows:
        ts = int(r['time'])
        if ts in seen:
            raise ValueError('Duplicate hourly candle')
        seen.add(ts)
        t = datetime.fromtimestamp(ts, UTC)
        if t > now + timedelta(minutes=5):
            raise ValueError('Future broker timestamp')
        if t.weekday() >= 5:
            continue
        o,h,l,c = (float(r[k]) for k in ('open','high','low','close'))
        if not all(math.isfinite(v) and v > 0 for v in (o,h,l,c)) or not l <= min(o,c) <= max(o,c) <= h:
            raise ValueError('Invalid broker OHLC')
        groups.setdefault(t.date(), []).append((t,o,h,l,c))
    result = []
    for day, bars in sorted(groups.items()):
        bars.sort()
        start = datetime.combine(day, datetime.min.time(), UTC)
        end = start + timedelta(days=1)
        if end <= now and len(bars) < 10:
            continue  # exclude short holiday/stub sessions, never pad missing history
        result.append(dict(openTimeUtc=stamp(start), closeTimeUtc=stamp(end),
            open=bars[0][1], high=max(b[2] for b in bars), low=min(b[3] for b in bars), close=bars[-1][4]))
    if sum(datetime.fromisoformat(b['closeTimeUtc'].replace('Z','+00:00')) <= now for b in result) < 220:
        raise ValueError('Insufficient completed broker history')
    return result

def collect():
    import MetaTrader5 as mt
    now = datetime.now(UTC)
    markets = {}
    try:
        if not mt.initialize(r'C:\Realtouch\MT5\terminal64.exe', timeout=15000):
            raise RuntimeError('MT5 initialization unavailable')
        a = mt.account_info()
        if not a or a.login != 477788709 or a.trade_mode != mt.ACCOUNT_TRADE_MODE_DEMO or a.server != 'Exness-MT5Trial9':
            raise RuntimeError('Expected VTB demo account not connected')
        if not mt.symbol_select('USOILm', True):
            raise RuntimeError('USOILm unavailable')
        rates = mt.copy_rates_from_pos('USOILm', mt.TIMEFRAME_H1, 0, 18000)
        if rates is None:
            raise RuntimeError('USOILm hourly history unavailable')
        bars = daily_from_hourly(rates, now)
        latest = datetime.fromisoformat(bars[-1]['openTimeUtc'].replace('Z','+00:00'))
        if now-latest > timedelta(days=4):
            raise ValueError('Broker history is stale')
        short = mt.copy_rates_from_pos('USOILm', mt.TIMEFRAME_M15, 0, 8000)
        if short is None or len(short) < 1600:
            raise ValueError('Insufficient USOILm intraday history')
        intraday = [dict(openTimeUtc=stamp(datetime.fromtimestamp(int(r['time']), UTC)),
            closeTimeUtc=stamp(datetime.fromtimestamp(int(r['time']), UTC)+timedelta(minutes=15)),
            **{k:float(r[k]) for k in ('open','high','low','close')}) for r in short]
        markets['WTI/USD'] = dict(provider='Exness', feedSymbol='USOILm', candles=bars, intraday=intraday)
    except Exception as e:
        markets['WTI/USD'] = dict(error=str(e))
    finally:
        mt.shutdown()
    try:
        # Preserve the backtested SPX cash index; avoid a silent CFD substitution.
        req = urllib.request.Request('https://query1.finance.yahoo.com/v8/finance/chart/%5EGSPC?interval=1d&range=5y',
                                     headers={'User-Agent':'Mozilla/5.0'})
        with urllib.request.urlopen(req, timeout=25) as r:
            raw = json.load(r)['chart']['result'][0]
        q = raw['indicators']['quote'][0]
        bars = []
        for i,t in enumerate(raw['timestamp']):
            values = [q[k][i] for k in ('open','high','low','close')]
            if any(v is None for v in values):
                continue
            bars.append(dict(timestamp=t, open=values[0],high=values[1],low=values[2],close=values[3]))
        if len(bars) < 220:
            raise ValueError('Insufficient cash-index history')
        markets['SPX/USD'] = dict(provider='Yahoo', feedSymbol='^GSPC', candles=bars)
    except Exception as e:
        markets['SPX/USD'] = dict(error=str(e))
    return dict(version=1, generatedAtUtc=stamp(datetime.now(UTC)), markets=markets)

def main():
    import boto3
    payload = collect()
    data = json.dumps(payload, allow_nan=False).encode()
    path = Path(r'C:\Realtouch\state\market-data-snapshot.json')
    path.write_bytes(data)
    boto3.client('s3', region_name='us-east-1').put_object(Bucket=BUCKET, Key=KEY, Body=data,
        ContentType='application/json', ServerSideEncryption='AES256')
    print(json.dumps({'publishedAtUtc':payload['generatedAtUtc'], 'markets':{
        k: {'bars':len(v.get('candles',[])), 'error':v.get('error')} for k,v in payload['markets'].items()}}), flush=True)

if __name__ == '__main__':
    while True:
        try:
            main()
        except Exception as e:
            print(type(e).__name__+': '+str(e), flush=True)
        time.sleep(120)
