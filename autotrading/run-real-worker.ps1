$ErrorActionPreference = 'Continue'
$realRoot = Split-Path -Parent $PSScriptRoot
$env:SSL_CERT_FILE = 'C:\Realtouch\autotrading\.venv\Lib\site-packages\botocore\cacert.pem'
& 'C:\Realtouch\autotrading\.venv\Scripts\python.exe' -u (Join-Path $PSScriptRoot 'exness_worker.py') --config (Join-Path $PSScriptRoot 'config.local.json') --allow-real-orders *>> (Join-Path $realRoot 'state\worker.log')
exit $LASTEXITCODE
