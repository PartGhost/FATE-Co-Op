$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Start-TestGame.ps1') -Instance a
& (Join-Path $PSScriptRoot 'Start-TestGame.ps1') -Instance b
& (Join-Path $PSScriptRoot 'Open-TestGrove.ps1') -Instance a
& (Join-Path $PSScriptRoot 'Open-TestGrove.ps1') -Instance b
& (Join-Path $PSScriptRoot 'Connect-TwoGames.ps1')
