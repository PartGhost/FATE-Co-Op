$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
if (Get-Process -Name FateReawakened -ErrorAction SilentlyContinue) { throw 'Close FATE first.' }
$steamApps = Join-Path (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath 'steamapps'
$source = Join-Path $steamApps 'common\FATE Reawakened'
$game = Join-Path $workspace 'test-game'
if ((Test-Path -LiteralPath $game) -and (Get-Content -LiteralPath (Join-Path $game 'version.txt') -Raw) -eq (Get-Content -LiteralPath (Join-Path $source 'version.txt') -Raw)) {
    Write-Output 'Test copies already match the installed FATE version.'
    return
}
foreach ($copy in $TestInstances | ForEach-Object { Get-TestGamePath $_ }) {
    if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy -Recurse -Force }
}
Remove-Item -Path (Join-Path $workspace 'artifacts\*.original') -Force -ErrorAction SilentlyContinue
robocopy $source $game /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying the game failed.' }
Copy-Item -Path (Join-Path $PSScriptRoot 'bepinex\*') -Destination $game -Recurse -Force
& (Join-Path $PSScriptRoot 'Prepare-TestInstances.ps1')
Write-Output ('Test copies rebuilt from FATE ' + (Get-Content -LiteralPath (Join-Path $source 'version.txt') -Raw).Trim() + '. Saves in test-saves are kept.')
