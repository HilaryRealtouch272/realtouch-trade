$ErrorActionPreference = 'Stop'
$realRoot = 'C:\RealtouchManual'
$realCode = Join-Path $realRoot 'autotrading'
$profilePath = Join-Path $realCode 'config.local.json'
$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
if ($profile.mode -ne 'live' -or $profile.expectedLogin -le 0 -or !$profile.expectedServer) { throw 'An explicitly bound live account profile is required.' }
if ($profile.sizingMode -ne 'fixed' -or $profile.fixedLots -ne 0.01 -or $profile.mainFixedLots -ne 0.01 -or $profile.mainExitMode -ne 'tp2') { throw 'Expected fixed 0.01 lots and main TP2. Nothing changed.' }
if ((Get-ScheduledTask RealtouchRealWorker).State -eq 'Running') { throw 'Pause entries and stop RealtouchRealWorker yourself first. Nothing changed.' }
$backupFolder = Join-Path $realRoot ('state\before-telegram-policy-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupFolder | Out-Null
foreach ($name in @('exness_worker.py','signal_feed.py','execution_reporter.py')) {
    & 'C:\Realtouch\autotrading\.venv\Scripts\python.exe' -m py_compile (Join-Path $PSScriptRoot $name)
    if ($LASTEXITCODE -ne 0) { throw 'Update syntax check failed. Nothing installed.' }
}
Stop-ScheduledTask RealtouchRealReporter
foreach ($name in @('exness_worker.py','signal_feed.py','execution_reporter.py')) {
    Copy-Item -LiteralPath (Join-Path $realCode $name) -Destination (Join-Path $backupFolder $name)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $realCode $name) -Force
}
$profile | Add-Member entryExecutionPolicy 'telegram' -Force
$profile | Add-Member mainEntryPolicy 'zone' -Force
$profile.entriesEnabled = $false
$temporaryProfile = $profilePath + '.tmp'
$profile | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryProfile -Encoding UTF8
Move-Item -LiteralPath $temporaryProfile -Destination $profilePath -Force
Write-Host 'Telegram-entry policy installed. Entries OFF; no worker started and no order sent.'
Write-Host 'Extra drift/spread/reward-risk filters and short entry timers are disabled in this policy.'
Write-Host 'Only current signed active Telegram instructions can authorize new entries. Existing records are not replayed.'
Write-Host 'Run start-real.ps1 -Action Check, then activate yourself with -Action Start if checks pass.'
