# Windows demo execution host

The GitHub scanner and existing 15-minute cron remain the signal source. EC2 hosts only desktop MT5 and the Python execution worker. The signed feed is the same feed backed by delivered Telegram entries.

Use a Windows x86-64 host with Python 3.12 and the pinned connector dependencies. The initial deployment uses one t3.small with standard CPU credits, a 30GB encrypted gp3 disk, IMDSv2, an SSM management role and RDP restricted to the owner's public IPv4 address. Do not upgrade the AWS account's Free plan automatically. Credits and the plan expiration are independent limits; review both before continued use.

The cloud launcher sets `SSL_CERT_FILE` for its own process to the installed AWS SDK's CA bundle (`botocore/cacert.pem`). The fresh Windows image's default root store could not validate GitHub Pages; the SDK bundle successfully validated the HTTPS connection. Certificate and hostname verification remain enabled. No Windows certificate-store or security-policy change is required.

## First connection

The owner signs desktop MT5 into the same Exness demo account, enables Algo Trading and allows the external Python API. Broker credentials stay in MT5. Verify `--check` before starting execution. The worker rejects any live account or mismatched demo login/server.

## Cutover

1. Prepare the runtime and verify the cloud demo connection with entries disabled and a separate temporary check database.
2. Stop the local worker, verify its process has exited, then create a consistent backup using SQLite's backup API. Do not copy an active database file without its WAL.
3. Transfer the latest backup and private signing configuration through encrypted, private storage. Retain the existing execution identifiers and broker position tickets. Do not copy PID, lock, log or stale spool files.
4. Set cloud paths, verify the signed feed and reconcile the existing demo positions before enabling new cloud entries. Never run local and cloud execution concurrently.
5. Run `start-cloud-demo.ps1` in the MT5 user's desktop session. A logon task can restart the worker after an unexpected process exit; the database lock prevents a second worker on that host.

Disconnect RDP without signing out to keep that desktop session active. A reboot requires a new desktop login unless a separately tested unattended startup arrangement has been configured. Do not claim reboot recovery until it has been verified against the actual demo terminal.

Current demo sizing is fixed 0.01 lot for VTB with the signal's original broker-side SL and single TP. Cash-risk caps are bypassed in this explicitly selected demo mode. Broker minimum volume and margin checks still apply. Main fixed-lot demo entries use 0.02 total: 0.01 at TP1 and 0.01 at TP2, both with the original SL.

## Demo execution notifications

`execution_reporter.py` reads broker-confirmed execution records and posts them to the configured beta Telegram group. It runs separately from the execution worker, keeps durable per-event delivery records, and requires a confirmed entry notice before a closure update. Existing open positions are labelled as already open; closed historical trades are not replayed. Ambiguous network deliveries are withheld from automatic resend for manual review. The reporter never connects to MT5 or sends orders. Keep its bot token in a private configuration file outside version control.

## Separate strategy accounts

For a split deployment, use `allowedFlows: ["vtb"]` for the existing account and `allowedFlows: ["main"]` for the second account. Each requires its own MT5 installation, bound demo login/server, worker task, spool directory, execution database, feed-status directory and reporter receipt database. Never copy the first account's execution database into the second account. Strategy filtering controls new entries; reconciliation continues for existing positions. Prepare and verify the second demo terminal before changing the running account's routing.
