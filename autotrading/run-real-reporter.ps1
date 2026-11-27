$ErrorActionPreference = 'Continue'
$realRoot = Split-Path -Parent $PSScriptRoot
& 'C:\Realtouch\autotrading\.venv\Scripts\python.exe' -u (Join-Path $PSScriptRoot 'execution_reporter.py') --config (Join-Path $PSScriptRoot 'telegram.private.json') *>> (Join-Path $realRoot 'state\reporter.log')
exit $LASTEXITCODE
