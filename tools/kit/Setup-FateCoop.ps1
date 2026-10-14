$ErrorActionPreference = 'Stop'
$appId = '3030720'
$workspace = $PSScriptRoot
$game = Join-Path $workspace 'test-game'
$kit = Join-Path $workspace 'kit'
if ($env:OneDrive -and $workspace.StartsWith($env:OneDrive, [StringComparison]::OrdinalIgnoreCase)) { throw 'Move this folder out of OneDrive first, for example to C:\FATE Co-Op.' }
if (Get-Process -Name FateReawakened -ErrorAction SilentlyContinue) { throw 'Close FATE first.' }
$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
$libraries = [regex]::Matches((Get-Content -LiteralPath (Join-Path $steam 'steamapps\libraryfolders.vdf') -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value.Replace('\\', '\') }
$manifest = $libraries | ForEach-Object { Join-Path $_ ('steamapps\appmanifest_' + $appId + '.acf') } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$manifest) { throw 'FATE Reawakened is not installed through Steam on this PC.' }
$installDir = [regex]::Match((Get-Content -LiteralPath $manifest -Raw), '"installdir"\s+"([^"]+)"').Groups[1].Value
$source = Join-Path (Split-Path -Parent $manifest) ('common\' + $installDir)
if (!(Test-Path -LiteralPath $game)) {
    Write-Output ('Copying ' + $source + ' to ' + $game + ' (about 3 GB)...')
    robocopy $source $game /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Copying the game failed.' }
}
Copy-Item -Path (Join-Path $kit 'bepinex\*') -Destination $game -Recurse -Force
& (Join-Path $kit 'dotnet\dotnet.exe') (Join-Path $kit 'patcher\SandboxPatcher.dll') $workspace (Join-Path $workspace 'test-saves\instance-a') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Patching the co-op copy failed.' }
New-Item -ItemType Directory -Force -Path (Join-Path $game 'BepInEx\plugins') | Out-Null
Copy-Item -LiteralPath (Join-Path $kit 'FateCoop.Prototype.dll') -Destination (Join-Path $game 'BepInEx\plugins') -Force
$option = '"' + (Join-Path $game 'FateReawakened.exe') + '" %command%'
Set-Clipboard -Value $option
Write-Output ''
Write-Output 'Co-op copy ready. Your real FATE install and saves are untouched.'
Write-Output 'Last step, in Steam: right-click FATE Reawakened > Properties > General > Launch Options, and paste (Ctrl+V):'
Write-Output ('    ' + $option)
Write-Output 'Then start FATE from Steam, make a new Original FATE character, and accept your friend''s invite.'
