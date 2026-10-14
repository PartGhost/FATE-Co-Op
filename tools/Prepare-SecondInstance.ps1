$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
if (Get-Process -Name FateReawakened -ErrorAction SilentlyContinue) { throw 'Close the test games before preparing the second instance.' }
$source = Join-Path $workspace 'test-game'
$target = Join-Path $workspace 'test-game-b'
if (!(Test-Path -LiteralPath $target)) {
    Copy-Item -LiteralPath $source -Destination $target -Recurse
}
# Unity's single-instance setting applies only to the disposable copies.
foreach ($game in @($source, $target)) {
    $boot = Join-Path $game 'FateReawakened_Data\boot.config'
    $lines = Get-Content -LiteralPath $boot | Where-Object { $_ -notmatch '^single-instance=' }
    [IO.File]::WriteAllLines($boot, [string[]]$lines)
}
$saveB = Join-Path $workspace 'test-saves\instance-b'
New-Item -ItemType Directory -Force -Path $saveB | Out-Null
$seed = Join-Path $workspace 'test-saves\instance-a\FateMainSave.save'
$destination = Join-Path $saveB 'FateMainSave.save'
# Seed once from the disposable sandbox character; future progression is independent.
if (!(Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $seed)) {
    Copy-Item -LiteralPath $seed -Destination $destination
}
& (Join-Path $PSScriptRoot 'Build-Prototype.ps1')
