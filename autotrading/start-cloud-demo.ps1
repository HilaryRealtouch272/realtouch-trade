param([string]$Root = 'C:\Realtouch')
$ErrorActionPreference = 'Stop'
$taskPython = Join-Path $Root 'autotrading\.venv\Scripts\python.exe'
$taskWorker = Join-Path $Root 'autotrading\exness_worker.py'
$taskConfig = Join-Path $Root 'autotrading\config.local.json'
if (-not (Test-Path -LiteralPath $taskConfig)) { throw 'Cloud demo configuration has not been migrated yet.' }
# Fresh Windows images may lack the public issuer chain used by GitHub Pages.
# Use the installed AWS SDK's maintained CA bundle with TLS validation enabled.
$taskCaBundle = Join-Path $Root 'autotrading\.venv\Lib\site-packages\botocore\cacert.pem'
if (-not (Test-Path -LiteralPath $taskCaBundle)) { throw 'Cloud trusted CA bundle is missing.' }
$env:SSL_CERT_FILE = $taskCaBundle
Set-Location -LiteralPath $Root
& $taskPython $taskWorker --config $taskConfig --check
if ($LASTEXITCODE -ne 0) { throw 'Cloud demo account verification failed.' }
# The worker owns an exclusive database lock and rechecks the demo account on every cycle.
& $taskPython -u $taskWorker --config $taskConfig *>> (Join-Path $Root 'state\worker.log')
if ($LASTEXITCODE -ne 0) { throw 'Cloud demo worker stopped; Task Scheduler may retry.' }
