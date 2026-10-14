param([switch]$Demo, [switch]$Probe, [switch]$DisconnectTest)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $PSScriptRoot 'dotnet\dotnet.exe'
$client = Join-Path $workspace 'src\FateCoop.Client\bin\Release\net8.0\FateCoop.Client.dll'
$arguments = @($client, '--workspace', $workspace)
if ($Demo) { $arguments += '--demo' }
if ($Probe) { $arguments += '--probe' }
if ($DisconnectTest) { $arguments += '--disconnect-test' }
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Local client failed; inspect its output.' }
