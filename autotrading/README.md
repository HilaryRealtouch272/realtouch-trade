# Exness demo autotrading

## Deployed signal feed

The deployed GitHub scanner now captures the same new entries that generate Telegram alerts. After Telegram confirms delivery, it publishes a signed `auto-trading-signals.json` snapshot. The desktop worker verifies its HMAC signature, entry age and original trade levels before accepting it. Existing positions, targets already reached, stale entries, unannounced entries and duplicates are not imported as new orders.

Use the existing GitHub scanner and cron. **Do not run a second scanner or migrate its state for this setup.** The older local-scanner instructions below describe a standalone development alternative only. For the deployed setup, configure `remoteFeedUrl` and the matching private `remoteFeedKey` in the ignored local configuration. The publishing key is provided through the `AUTOTRADING_FEED_SIGNING_KEY` GitHub Actions secret; no broker credentials or account information are published.

Run `exness_worker.py --config config.local.json --feed-check` to verify the feed without trading. `--status` includes the latest feed status and broker execution records. The worker polls the signed feed every 30 seconds and reconciles its actual broker positions every five seconds. Between the existing 15-minute scans, a signed snapshot older than five minutes is treated as waiting for the next scan, never as permission to enter old signals.

The account owner elected to keep the $20 demo balance and the existing demo risk caps. Orders below the broker's minimum volume are rejected, never rounded up to force an execution. This is expected risk-limit behavior, not a lost connection. The worker runs on this Windows computer and stops when the computer is off; AWS hosting has not been installed.

This adds broker execution to **new qualified main and VTB signal events**. The existing Telegram gates, strategy rules and paper ledger are unchanged. The signal producer is enabled in `backend/appsettings.json`; the worker configuration enables **demo** entries. No live mode exists. A matching, explicitly configured Exness demo login/server and symbol map are required before an order can be sent.

The worker has not been connected to the owner's demo account or tested against Exness yet. The automated tests use a simulated broker. Phone MT5 can monitor the same account but cannot run this worker.

## Local demo setup

1. On a Windows computer, install desktop MT5 and sign into your **Exness demo**. Enable algorithmic trading and allow the external Python API in MT5's Expert Advisors settings. Use a hedging account; independent target legs are not supported on netting accounts. Keep the real account disconnected from this terminal.
2. From the repository root, run `./autotrading/start-demo.ps1 -Action Inspect`. This creates an isolated Python environment, installs MetaQuotes' connector, creates the ignored `config.local.json`, and reads the demo account's login, server and available symbol names. It sends **no orders**. Use 64-bit Python 3.12 on Windows. The dependency file keeps NumPy below version 2 for connector compatibility.
3. In `autotrading/config.local.json`, set `expectedLogin`, `expectedServer` and `terminalPath` (full path to this MT5 terminal executable). Fill `symbols` with mappings verified by Inspect, for example `"BTC/USDT": "BTCUSDm"` **only if that exact symbol is present on your account**. Map each supported main/VTB market separately; unavailable markets are rejected. Do not enter passwords in the file or send them in chat. MT5 holds the account login.
4. Run `./autotrading/start-demo.ps1 -Action Check`. It verifies the exact demo account without placing orders.
5. Run `./autotrading/start-demo.ps1 -Action Run` to start execution. It checks positions every five seconds and watches `backend/.autotrading/spool`. Use `-Action Status` separately to inspect actual execution records and rejection reasons. Ctrl+C stops the worker.
6. The **same Windows machine** must run the backend scanner to produce the private spool. Use a single scanner owner. Before running the backend against production Telegram credentials/state, stop the existing external cron and migrate its current `.cache` state to that scanner. Do not start a second scanner with independent empty alert state: it could resend Telegram alerts. This change does not perform that migration or modify the existing GitHub cron.

For a local isolated test, use separate backend state and a test Telegram chat. Start the backend from `backend` with the existing market-data configuration (`dotnet run`). No old ledger rows or old Telegram messages are imported. Once this checkout's scanner emits a new qualified alert event, the worker considers it for demo execution. The current deployed GitHub workflow does **not** send events to this computer. Do not expect running the worker alone to execute its remote alerts.

`AutoTrading:SpoolDirectory` can override the handoff folder with an absolute private directory. The worker's `spoolDirectory` must point to the same folder. Relative paths in its configuration resolve against the configuration file's directory. Execution files and the SQLite database are outside `.cache` and are never included in the current Pages/bot-state outputs. Keep this directory private and on a local disk, not a synchronized/network drive. Never delete the execution database to clear a block or retry a signal.

## Demo risk and execution rules

- Proposed **demo-only** limits: 0.25% equity planned stop risk per full-size signal, 0.5% combined account risk, and a 1% daily account loss block, using the Lagos calendar day. VTB applies its existing side/market risk multiplier. The sizing reserves 10% of its risk budget for execution costs. Gaps, slippage and commissions can still exceed the estimate; these are not guaranteed maximum losses.
- Main uses its existing **25% / 50% / 25%** target weights with the original stop on each leg. VTB uses one stop and target. All legs are preflighted before submission. If even one target leg is below minimum lot size, the signal is rejected, never rounded up. A small balance may therefore produce no executable trades.
- Only one position group per broker symbol across both streams. Existing manual positions and pending orders also block overlap; all account positions count towards risk. A position without a stop blocks new entries. No martingale, averaging or automatic position increases.
- New entries are blocked on Sundays in Lagos. Stops, targets, reconciliation and time exits continue. This does not add the previously discussed VTB cooldown.
- Main events have a five-minute entry deadline. VTB has fifteen minutes from the completed daily candle close, **not** from the bar's opening date or a delayed alert. A later scan can still create a paper row, but its old VTB entry will be rejected for execution. Drift above 0.10 of original stop distance, spread above 0.10 of that distance, a stale quote, an invalid broker stop/target, insufficient remaining reward/risk, or an entry outside the main zone also rejects the trade. Missed signals are not chased.
- Main remaining positions time-exit after the paper ledger's holding window (30 analysis candles from acceptance). VTB time-exits at the close of its 20th holding day. Stops and targets are sent to the broker with every entry and remain there if the worker stops; **time exits require the worker and MT5 to be online**.
- SQLite remembers every signal and each target leg across restarts. A crash/timeout around order submission causes reconciliation and blocks new entries until the broker outcome is proven. Uncertain orders are never automatically resubmitted. Inspect MT5 positions/orders/deals before resolving an uncertain state; do not delete the database.
- Actual broker tickets, filled entry prices, volume, stops, closure confirmation and net account-currency P&L are stored in the private execution database. The dashboard paper ledger remains a simulation and is **not** a display of broker fills. Broker history, not paper TP/SL notices, determines actual execution closures. No additional Telegram messages are sent by this worker.

Set `entriesEnabled` to `false` and restart the worker to pause new entries while continuing to track and close existing demo positions. To disable signal production, set `AutoTrading__Enabled=false` for the backend. Stopping the worker does not close open positions. Keep the backend, MT5 and worker supervised; a console window alone is not a reliable production deployment.

## AWS later

Use a Windows EC2 server with desktop MT5, this checkout, .NET 10 and 64-bit Python. Choose a server only after checking the account's actual AWS credit allowance and the full Windows, disk and network costs. Disconnecting Remote Desktop can leave applications running; signing out or shutting down stops them. Configure startup/recovery for the scanner and worker, verify it on demo, and move scanner ownership/state before resuming alerts. Do not run competing scanners on GitHub and AWS. AWS provisioning, startup tasks and billing settings are not installed by this change.

## Verification

```
dotnet test backend.Tests/RealtouchSmartTrade.Tests.csproj
python -m unittest discover -s autotrading -v
```

Official interfaces used: [MetaQuotes order_send](https://www.mql5.com/en/docs/python_metatrader5/mt5ordersend_py), [account-currency profit calculation](https://www.mql5.com/en/docs/python_metatrader5/mt5ordercalcprofit_py), and [account information](https://www.mql5.com/en/docs/python_metatrader5/mt5accountinfo_py). This implementation uses MT5, not the separate Exness Terminal API.
