# Owner activation on AWS

This folder is prepared software, not an activated trading service. Existing demo
workers keep their own code, MT5 terminals, configurations and execution databases.
No real worker/task is created until the owner runs Start below.

Profile: both main and VTB on the same real Exness hedging account, exactly 0.01
lot per trade, main full exit at TP2, VTB full exit at its single target, original
signal stop-loss, no partial target legs. WTI is blocked in code even if mapped.
SPX execution is not enabled. Unsupported minimum lots are rejected, not rounded up.
Fixed volume does not cap cash losses; the loss at the stop depends on the market
and stop distance. Gaps and slippage can increase it.

The Telegram execution policy follows current signed, still-active delivered entry
instructions. It has no additional drift/spread/reward-risk filter or 5/15-minute
entry timer. Main prices must remain inside the signal's entry zone; an outside-zone
instruction waits and rechecks. Closed/cancelled instructions and old ledger rows
cannot open new trades. Source freshness is checked before sending Telegram.
Feed freshness, original SL/TP validity, broker constraints, duplicate protection,
WTI exclusion and weekend policy remain enforced. An execution attempt is not a
guarantee of a broker fill.

For explicitly selected immediate market entry, apply the staged update using
`Apply-TelegramPolicy.ps1 -MainEntryPolicy market` while the worker is stopped.
This removes the wait for the main entry zone. It submits at the available broker
price with the same original SL and TP2; prices and the resulting risk/reward can
differ from the Telegram reference. It does not rebase crossed or invalid stops or
targets, and broker constraints still apply. The update leaves entries OFF.

## On the remote Windows desktop

1. Open **MT5 REAL - MANUAL ONLY**. Confirm it is the real account you intend to
   use. Keep the two demo terminals signed into their existing demo accounts.
2. Open **Windows PowerShell as Administrator** on the remote computer. Paste:

```powershell
$realLogin = [long](Read-Host 'Real MT5 login number')
$realServer = Read-Host 'Exact real MT5 server name'
& C:\RealtouchManual\autotrading\start-real.ps1 -Action Setup -LoginNumber $realLogin -Server $realServer
& C:\RealtouchManual\autotrading\start-real.ps1 -Action Check
```

Enter only the account number/server in these prompts. Your password stays in MT5.
Setup copies the existing private signed feed and beta-group credentials locally,
excludes WTI/SPX, creates a separate real profile with entries disabled, and starts
nothing. Do not repeat Setup after a profile exists. Check verifies the exact real
account and signed feed without submitting or queuing orders. If either check
fails, do not proceed. A feed marked stale between scans cannot authorize entries.

3. To activate yourself, run:

```powershell
& C:\RealtouchManual\autotrading\start-real.ps1 -Action Start
```

Read the displayed account/settings and type the requested **START REAL accountnumber**
phrase only if correct. This is the step that enables real-money execution. It
creates/starts RealtouchRealWorker and RealtouchRealReporter. The reporter sends
broker-confirmed entry/exit notices to the existing beta group, labelled LIVE.
No old Telegram signals are replayed. New entries still require valid signed fresh
instructions, delivered entry alerts, available margin, valid stops, fresh broker quotes,
the main entry zone and an available explicitly mapped broker symbol.

4. Check status:

```powershell
& C:\RealtouchManual\autotrading\start-real.ps1 -Action Status
```

Running tasks alone do not prove a trade filled. Check the actual MT5 Trade/History
tab and beta execution notice. The dashboard signal ledger is not broker history.
Do not clear the execution database or spool to retry a missed signal.

To pause new entries while continuing existing-position management:

```powershell
& C:\RealtouchManual\autotrading\start-real.ps1 -Action Pause
```

Pause takes effect on the next worker cycle and cannot withdraw an order already
being submitted. SL/TP remain at the broker; time exits require the worker online.
Disconnect RDP, do not sign out. Following a Windows reboot, sign back into the
desktop, confirm MT5 connections and check task status. Unattended reboot recovery
has not been verified. Keep the profile, feed key and Telegram token private.
