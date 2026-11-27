param(
    [ValidateSet('Setup','Check','Start','Pause','Status')][string]$Action = 'Check',
    [string]$Root = 'C:\RealtouchManual',
    [long]$LoginNumber = 0,
    [string]$Server = ''
)
$ErrorActionPreference = 'Stop'
$realPython = 'C:\Realtouch\autotrading\.venv\Scripts\python.exe'
$realCode = Join-Path $Root 'autotrading'
$realConfig = Join-Path $realCode 'config.local.json'
$realWorker = Join-Path $realCode 'exness_worker.py'
$realCa = 'C:\Realtouch\autotrading\.venv\Lib\site-packages\botocore\cacert.pem'
$env:SSL_CERT_FILE = $realCa
$realState = Join-Path $Root 'state'
if ($Action -eq 'Setup') {
    if ($LoginNumber -le 0 -or !$Server) { throw 'Provide -LoginNumber and -Server from the REAL account; do not provide a password.' }
    if (Test-Path -LiteralPath $realConfig) { throw 'Real profile already exists. Do not overwrite account binding or execution history.' }
    $realTemplate = Get-Content -LiteralPath 'C:\Realtouch\autotrading\config.local.json' -Raw | ConvertFrom-Json
    $realTemplate | Add-Member mode 'live' -Force
    $realTemplate | Add-Member entriesEnabled $false -Force
    $realTemplate | Add-Member expectedLogin $LoginNumber -Force
    $realTemplate | Add-Member expectedServer $Server -Force
    $realTemplate | Add-Member terminalPath (Join-Path $Root 'MT5\terminal64.exe') -Force
    $realTemplate | Add-Member allowedFlows @('main','vtb') -Force
    $realTemplate | Add-Member sizingMode 'fixed' -Force
    $realTemplate | Add-Member fixedLots 0.01 -Force
    $realTemplate | Add-Member mainFixedLots 0.01 -Force
    $realTemplate | Add-Member mainExitMode 'tp2' -Force
    $realTemplate | Add-Member mainEntryPolicy 'zone' -Force
    $realTemplate | Add-Member entryExecutionPolicy 'telegram' -Force
    $realTemplate | Add-Member terminalPortable $true -Force
    $realTemplate | Add-Member stateDatabase (Join-Path $realState 'execution.sqlite3') -Force
    $realTemplate | Add-Member spoolDirectory (Join-Path $realState 'spool') -Force
    $realTemplate.symbols.PSObject.Properties.Remove('WTI/USD')
    $realTemplate.symbols.PSObject.Properties.Remove('SPX/USD')
    if (!$realTemplate.remoteFeedUrl -or !$realTemplate.remoteFeedKey) { throw 'The existing private signed-feed configuration is missing.' }
    New-Item -ItemType Directory -Path $realState -Force | Out-Null
    $realTemplate | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $realConfig -Encoding UTF8
    $realReporter = Get-Content -LiteralPath 'C:\Realtouch\autotrading\telegram.private.json' -Raw | ConvertFrom-Json
    $realReporter | Add-Member executionDatabase (Join-Path $realState 'execution.sqlite3') -Force
    $realReporter | Add-Member receiptDatabase (Join-Path $realState 'telegram-receipts.sqlite3') -Force
    $realReporter | Add-Member accountLabel ('REAL ' + $LoginNumber) -Force
    $realReporter | Add-Member executionMode 'live' -Force
    $realReporter | Add-Member mainExitMode 'tp2' -Force
    if ([string]$realReporter.chatId -ne '-5446001871') { throw 'Unexpected beta group. Setup stays paused.' }
    $realReporter | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $realCode 'telegram.private.json') -Encoding UTF8
    Write-Host 'Profile prepared with entries OFF. No task started and no order sent. Run Check next.'
    exit
}
if (!(Test-Path -LiteralPath $realConfig)) { throw 'Run Setup first.' }
if ($Action -eq 'Status') {
    & $realPython $realWorker --config $realConfig --status
    Get-ScheduledTask -TaskName RealtouchRealWorker,RealtouchRealReporter -ErrorAction SilentlyContinue | Select-Object TaskName,State
    exit
}
if ($Action -eq 'Check') {
    & $realPython $realWorker --config $realConfig --check
    if ($LASTEXITCODE -ne 0) { throw 'Account check failed. Do not start execution.' }
    & $realPython $realWorker --config $realConfig --feed-check
    if ($LASTEXITCODE -ne 0) { throw 'Signed feed check failed. Do not start execution.' }
    Write-Host 'Read-only checks finished. No orders sent.'
    exit
}
$realSettings = Get-Content -LiteralPath $realConfig -Raw | ConvertFrom-Json
if ($Action -eq 'Start') {
    & $realPython $realWorker --config $realConfig --check
    if ($LASTEXITCODE -ne 0) { throw 'Account verification failed.' }
    if (!(Test-Path -LiteralPath (Join-Path $realCode 'telegram.private.json'))) { throw 'Beta reporter configuration is missing.' }
    Write-Host ('REAL account ' + $realSettings.expectedLogin + ': 0.01 lot, main TP2, VTB single target, no partial exits, NO WTI.')
    Write-Host 'Fixed volume does not cap cash losses. This starts automatic real-money orders from fresh signals.'
    $ownerConfirm = Read-Host ('To activate yourself, type START REAL ' + $realSettings.expectedLogin)
    if ($ownerConfirm -cne ('START REAL ' + $realSettings.expectedLogin)) { throw 'Not activated.' }
    $realSettings.entriesEnabled = $true
    $realSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $realConfig -Encoding UTF8
    $realPrincipal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
    $realTrigger = New-ScheduledTaskTrigger -AtLogOn -User $realPrincipal.UserId
    $realTaskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    foreach ($role in @('Worker','Reporter')) {
        $realLauncher = Join-Path $realCode ('run-real-' + $role.ToLowerInvariant() + '.ps1')
        $realTaskAction = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -File "' + $realLauncher + '"') -WorkingDirectory $Root
        Register-ScheduledTask -TaskName ('RealtouchReal' + $role) -Action $realTaskAction -Principal $realPrincipal -Trigger $realTrigger -Settings $realTaskSettings -Force | Out-Null
        Start-ScheduledTask -TaskName ('RealtouchReal' + $role)
    }
    Write-Host 'Start requested by account owner. Run Status and verify broker-confirmed fills; do not assume an order was placed.'
} elseif ($Action -eq 'Pause') {
    $realSettings.entriesEnabled = $false
    $realSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $realConfig -Encoding UTF8
    # The worker reloads entriesEnabled each cycle; do not kill a submission mid-flight.
    Write-Host 'New entries paused from the next cycle. Existing positions continue to be tracked.'
}
