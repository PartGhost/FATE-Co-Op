param([Parameter(Mandatory)][string]$Command, [ValidateSet('a','b','c')][string]$Instance = 'a')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
$queue = Join-Path (Get-RuntimePath $Instance) 'commands'
New-Item -ItemType Directory -Force -Path $queue | Out-Null
$name = [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff') + '-' + [Guid]::NewGuid().ToString('N')
$staged = Join-Path $queue ($name + '.tmp')
[IO.File]::WriteAllText($staged, $Command)
Move-Item -LiteralPath $staged -Destination (Join-Path $queue ($name + '.cmd'))
