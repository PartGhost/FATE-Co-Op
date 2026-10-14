param([ValidateSet('a','b')][string]$Instance = 'a')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$game = Join-Path $workspace $(if ($Instance -eq 'a') { 'test-game' } else { 'test-game-b' })
$manifest = Get-Content -LiteralPath (Join-Path $game 'fate-coop-sandbox.json') -Raw | ConvertFrom-Json
$expectedSave = Join-Path $workspace ('test-saves\instance-' + $Instance)
if ($manifest.saveDirectory -ne $expectedSave) { throw 'Sandbox manifest is invalid.' }
foreach ($assembly in $manifest.assemblies) {
    $actual = Get-FileHash -LiteralPath (Join-Path $game ('FateReawakened_Data\Managed\' + $assembly.assembly))
    if ($actual.Hash -ne $assembly.sha256) { throw ('Sandbox assembly hash mismatch: ' + $assembly.assembly) }
}
if (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx\plugins\FateCoop.Prototype.dll'))) { throw 'Build the prototype first.' }
$existing = Get-Process -Name FateReawakened -ErrorAction SilentlyContinue
if ($existing | Where-Object { $_.Path -eq (Join-Path $game 'FateReawakened.exe') -or !$_.Path -or !$_.Path.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase) }) { throw 'This instance or a non-sandbox FATE process is already running.' }
$runtime = Join-Path $workspace $(if ($Instance -eq 'a') { 'artifacts\runtime' } else { 'artifacts\runtime-b' })
$archive = Join-Path $runtime ('runs\' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff'))
New-Item -ItemType Directory -Force -Path $archive | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $archive 'commands') | Out-Null
$oldQueue = Join-Path $runtime 'commands'
if (Test-Path -LiteralPath $oldQueue) {
    foreach ($command in Get-ChildItem -LiteralPath $oldQueue -File) {
        Move-Item -LiteralPath $command.FullName -Destination (Join-Path $archive ('commands\' + $command.Name))
    }
}
foreach ($name in @('status.json','telemetry.jsonl','route-result.json','primary-walk-result.json','verification.json','lifecycle-result.json','Player.log')) {
    $previous = Join-Path $runtime $name
    if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination (Join-Path $archive $name) }
}
$steamApps = Join-Path (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath 'steamapps'
$appManifest = Get-ChildItem -LiteralPath $steamApps -Filter 'appmanifest_*.acf' | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '"installdir"\s+"FATE Reawakened"' }
if (!$appManifest) { throw 'FATE Reawakened Steam manifest not found.' }
$env:SteamAppId = $appManifest.BaseName.Substring('appmanifest_'.Length)
$log = Join-Path $runtime 'Player.log'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $log) | Out-Null
$arguments = '-screen-fullscreen 0 -screen-width 960 -screen-height 540 -logFile "' + $log + '"'
$process = Start-Process -FilePath (Join-Path $game 'FateReawakened.exe') -WorkingDirectory $game -ArgumentList $arguments -PassThru
$process.Id | Set-Content (Join-Path $runtime 'process-id.txt')
Write-Output ('Started isolated FATE instance ' + $Instance + ', process ' + $process.Id)
