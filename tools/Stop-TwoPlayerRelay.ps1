$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
[IO.File]::WriteAllText((Join-Path $workspace 'artifacts\pair-relay-stop'), 'stop')
Write-Output 'Requested a graceful relay stop and removal of remote guests.'
