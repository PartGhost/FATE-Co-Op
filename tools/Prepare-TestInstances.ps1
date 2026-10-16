$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
if (Get-Process -Name FateReawakened -ErrorAction SilentlyContinue) { throw 'Close the test games before preparing the extra instances.' }
$source = Get-TestGamePath 'a'
$seed = Join-Path (Get-SavePath 'a') 'FateMainSave.save'
foreach ($instance in $TestInstances) {
    $game = Get-TestGamePath $instance
    if (!(Test-Path -LiteralPath $game)) { Copy-Item -LiteralPath $source -Destination $game -Recurse }
    # Unity's single-instance setting applies only to the disposable copies.
    $boot = Join-Path $game 'FateReawakened_Data\boot.config'
    [IO.File]::WriteAllLines($boot, [string[]](Get-Content -LiteralPath $boot | Where-Object { $_ -notmatch '^single-instance=' }))
    $saves = Get-SavePath $instance
    New-Item -ItemType Directory -Force -Path $saves | Out-Null
    $destination = Join-Path $saves 'FateMainSave.save'
    # Seed once from instance A's character; future progression is independent.
    if (!(Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $seed)) { Copy-Item -LiteralPath $seed -Destination $destination }
}
& (Join-Path $PSScriptRoot 'Build-Prototype.ps1')
