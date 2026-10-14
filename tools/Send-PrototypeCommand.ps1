param([Parameter(Mandatory)][string]$Command, [ValidateSet('a','b')][string]$Instance = 'a')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$queue = Join-Path $workspace $(if ($Instance -eq 'a') { 'artifacts\runtime\commands' } else { 'artifacts\runtime-b\commands' })
New-Item -ItemType Directory -Force -Path $queue | Out-Null
$name = [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff') + '-' + [Guid]::NewGuid().ToString('N')
$staged = Join-Path $queue ($name + '.tmp')
[IO.File]::WriteAllText($staged, $Command)
Move-Item -LiteralPath $staged -Destination (Join-Path $queue ($name + '.cmd'))
