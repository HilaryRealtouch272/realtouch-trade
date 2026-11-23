# WTI / SPX private candle bridge

The existing AWS VTB MT5 demo terminal provides **USOILm** history. The read-only
exporter normalizes hourly candles into UTC daily bars and publishes recent M15
candles for the main workspace. It never submits orders or changes account login.
SPX remains the original **Yahoo ^GSPC cash index**, fetched from AWS; US500 CFD
candles are deliberately not substituted for the backtested index.

A separate `RealtouchMarketDataExporter` Windows task uploads one encrypted JSON
object to a private S3 key every two minutes. Existing workers and their execution
databases are independent. GitHub Actions assumes a repository-environment-bound
OIDC role permitted to read only that object. The bucket is not public; no broker
password or Telegram token is uploaded.

`MarketData:SnapshotPath` enables the bridge. Missing/invalid snapshots, errors,
wrong instrument identities or snapshots older than 20 minutes block affected
market entries; they never silently fall back to old Yahoo candles. BTC, ETH,
gold and the main model rules are unchanged. WTI uses the same stop, target,
weekend and risk rules; the first broker-feed entry requires a newly confirmed
bar and cannot backfill an old replay position. Existing futures-reference WTI
positions must not be settled against broker CFD candles. There were no open WTI
positions at this cutover. SPX broker execution remains disabled.

`vtb-feed-status.json` and `/api/vtb-feed-status` expose market-data health separately
from open positions. A successful dashboard deployment alone does not prove feeds
are healthy. The exporter, feeds and existing workers must all be verified after
release. After an AWS reboot, the interactive Windows desktop must be logged in.

Checks:
- `python -m unittest discover -s marketdata -p test_exness_snapshot.py`
- `dotnet test backend.Tests --filter FullyQualifiedName~MarketHistoryBridge`
