param([string[]]$Instances = @('a', 'b'))
$ErrorActionPreference = 'Stop'
foreach ($instance in $Instances) { & (Join-Path $PSScriptRoot 'Start-TestGame.ps1') -Instance $instance }
foreach ($instance in $Instances) { & (Join-Path $PSScriptRoot 'Open-TestGrove.ps1') -Instance $instance }
& (Join-Path $PSScriptRoot 'Connect-Games.ps1') -Instances $Instances
