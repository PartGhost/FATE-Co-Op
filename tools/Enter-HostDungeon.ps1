param([string[]]$Instances = @('a', 'b'))
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State([string]$Instance) { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command, [string]$Instance) { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-All([scriptblock]$Condition, [string]$What, [int]$Seconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 400
        try { $states = @($Instances | ForEach-Object { State $_ }) } catch { continue }
        if (& $Condition $states) { return $states }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: ' + (($states | Select-Object gameScene, dungeonDepth, worldKey, ownsWorld, transitioning, status) | ConvertTo-Json -Compress))
}
foreach ($instance in $Instances) {
    if (!(Get-Process -Name FateReawakened -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path (Get-TestGamePath $instance) 'FateReawakened.exe') })) { throw ('Test game ' + $instance + ' is not running.') }
}
$hostState = State $Instances[0]
if ($hostState.gameScene -ne 'Dungeon' -or !$hostState.ownsWorld -or $hostState.dungeonRealm -ne 'Grove') {
    foreach ($instance in $Instances) { if ((State $instance).gameScene -eq 'Dungeon') { Send 'town' $instance } }
    Wait-All { param($s) @($s | Where-Object { $_.gameScene -ne 'Town_Grove' -or $_.transitioning }).Count -eq 0 } 'Everyone in town' | Out-Null
    Send 'enter-dungeon' $Instances[0]
    Wait-All { param($s) $s[0].gameScene -eq 'Dungeon' -and !$s[0].transitioning -and $s[0].worldKey -ne '' } 'Host on floor 1' | Out-Null
}
while ((State $Instances[0]).monstersTracked - (State $Instances[0]).trackedDead -lt 8) {
    $depth = (State $Instances[0]).dungeonDepth
    Send 'test-resume' $Instances[0]
    Send 'next-floor' $Instances[0]
    Wait-All { param($s) $s[0].dungeonDepth -gt $depth -and !$s[0].transitioning -and $s[0].worldKey -ne '' -and $s[0].monstersTracked -gt 0 } 'Host on a fresh floor' | Out-Null
}
$hostKey = (State $Instances[0]).worldKey
foreach ($instance in $Instances | Select-Object -Skip 1) {
    Send 'test-resume' $instance
    if ((State $instance).worldKey -eq $hostKey) { continue }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (!(State $instance).partnerElsewhere -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Send ('follow-partner ' + (State $Instances[0]).myId) $instance
}
$together = Wait-All { param($s) @($s | Where-Object { $_.worldKey -ne $hostKey -or $_.transitioning -or $_.prototypeActors -ne $Instances.Count - 1 }).Count -eq 0 } 'Everyone on the host floor'
foreach ($instance in $Instances) { Send 'test-resume' $instance }
Write-Output ('Everyone is on the host''s floor ' + $together[0].dungeonDepth + '.')
