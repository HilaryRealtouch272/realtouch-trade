$ErrorActionPreference = 'Stop'
$realRoot = 'C:\RealtouchManual'
$realCode = Join-Path $realRoot 'autotrading'
$profilePath = Join-Path $realCode 'config.local.json'
$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
if ($profile.mode -ne 'live' -or $profile.expectedLogin -le 0 -or !$profile.expectedServer) { throw 'An explicitly bound live account profile is required. Nothing changed.' }
if ((Get-ScheduledTask -TaskName RealtouchRealWorker).State -eq 'Running') {
    throw 'Pause entries and stop RealtouchRealWorker yourself first. Nothing changed.'
}
$backupPath = Join-Path $realRoot ('state\worker-before-zone-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.py')
Copy-Item -LiteralPath (Join-Path $realCode 'exness_worker.py') -Destination $backupPath
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'exness_worker.py') -Destination (Join-Path $realCode 'exness_worker.py') -Force
$profile | Add-Member mainEntryPolicy 'zone' -Force
$profile.entriesEnabled = $false
$temporaryProfile = $profilePath + '.tmp'
$profile | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporaryProfile -Encoding UTF8
Move-Item -LiteralPath $temporaryProfile -Destination $profilePath -Force
& 'C:\Realtouch\autotrading\.venv\Scripts\python.exe' -m py_compile (Join-Path $realCode 'exness_worker.py')
if ($LASTEXITCODE -ne 0) { throw 'Code check failed. Entries remain OFF.' }
Write-Host 'Main entry policy: published signal zone, without the additional drift cap. Entries remain OFF.'
Write-Host 'Fixed 0.01, main TP2, no partial exits, original stops, WTI exclusion and broker checks unchanged.'
Write-Host 'Run start-real.ps1 -Action Check, then activate yourself with -Action Start if checks pass.'
