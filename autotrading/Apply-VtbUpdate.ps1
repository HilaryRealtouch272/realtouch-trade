$ErrorActionPreference = 'Stop'
$updateRoot = $PSScriptRoot
$realRoot = 'C:\RealtouchManual'
$realCode = Join-Path $realRoot 'autotrading'
$profilePath = Join-Path $realCode 'config.local.json'
$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
if ($profile.mode -ne 'live' -or $profile.expectedLogin -le 0 -or !$profile.expectedServer) { throw 'An explicitly bound live account profile is required. Nothing changed.' }
if ((Get-ScheduledTask -TaskName RealtouchRealWorker).State -eq 'Running') {
    throw 'Pause entries and stop RealtouchRealWorker yourself first. Nothing changed.'
}
$backupFolder = Join-Path $realRoot ('state\code-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupFolder | Out-Null
foreach ($name in @('exness_worker.py','signal_feed.py')) {
    $destination = Join-Path $realCode $name
    Copy-Item -LiteralPath $destination -Destination (Join-Path $backupFolder $name)
    Copy-Item -LiteralPath (Join-Path $updateRoot $name) -Destination $destination -Force
}
$profile | Add-Member terminalPortable $true -Force
$profile.entriesEnabled = $false
$temporaryProfile = $profilePath + '.tmp'
$profile | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryProfile -Encoding UTF8
Move-Item -LiteralPath $temporaryProfile -Destination $profilePath -Force
& 'C:\Realtouch\autotrading\.venv\Scripts\python.exe' -m py_compile (Join-Path $realCode 'exness_worker.py') (Join-Path $realCode 'signal_feed.py')
if ($LASTEXITCODE -ne 0) { throw 'Code check failed. Entries remain OFF.' }
Write-Host 'Code updated. Entries OFF. No worker started. Broker positions and execution database unchanged.'
Write-Host 'Open the real MT5 terminal, then run start-real.ps1 -Action Check. If it fails, share the error code only.'
