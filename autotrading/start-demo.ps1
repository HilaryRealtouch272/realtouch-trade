param(
    [ValidateSet('Inspect', 'Check', 'Run', 'Status')]
    [string]$Action = 'Check'
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskConfig = Join-Path $taskRoot 'config.local.json'
if (-not (Test-Path -LiteralPath $taskConfig)) {
    Copy-Item -LiteralPath (Join-Path $taskRoot 'config.example.json') -Destination $taskConfig
}
$taskPython = Join-Path $taskRoot '.venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $taskPython)) {
    python -m venv (Join-Path $taskRoot '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create Python environment' }
}
$taskDependencyMarker = Join-Path $taskRoot '.venv/dependencies-ready'
if (-not (Test-Path -LiteralPath $taskDependencyMarker)) {
    & $taskPython -m pip install -r (Join-Path $taskRoot 'requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Could not install MT5 connector' }
    Set-Content -LiteralPath $taskDependencyMarker -Value 'MT5 demo dependencies installed'
}
$taskArguments = @((Join-Path $taskRoot 'exness_worker.py'), '--config', $taskConfig)
switch ($Action) {
    'Inspect' { $taskArguments += '--inspect-demo' }
    'Check' { $taskArguments += '--check' }
    'Status' { $taskArguments += '--status' }
    'Run' { Write-Host 'Starting DEMO execution. Live accounts are blocked. Ctrl+C stops the worker.' }
}
& $taskPython @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Demo worker stopped with an error; check the message above' }
