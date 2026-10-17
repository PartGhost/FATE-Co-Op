$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
$all = @('a', 'b', 'c')
function State([string]$Instance) { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-All([scriptblock]$Condition, [string]$What, [int]$Seconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 400
        try { $s = @($all | ForEach-Object { State $_ }) } catch { continue }
        if (& $Condition $s[0] $s[1] $s[2]) { return $s }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: ' + (($s | Select-Object myId, worldOwner, ownsWorld, gameScene, dungeonDepth, worldKey, partnerCount, partnersInWorld, monstersTracked, trackedHp, trackedDead, hitsApplied, status) | ConvertTo-Json -Compress))
}
function Same($x, $y) { $x.worldKey -ne '' -and $x.worldKey -eq $y.worldKey -and $x.monstersTracked -gt 0 -and $x.monstersTracked -eq $y.monstersTracked -and [Math]::Abs($x.trackedHp - $y.trackedHp) -le 1 -and $x.trackedDead -eq $y.trackedDead }
function Hit-Until([string]$Instance, [scriptblock]$Done, [string]$What) {
    for ($i = 0; $i -lt 200; $i++) {
        $s = @($all | ForEach-Object { State $_ })
        if (& $Done $s[0] $s[1] $s[2]) { return }
        Send 'test-hit' $Instance
        Start-Sleep -Milliseconds 150
    }
    throw ($What + ' never happened.')
}

$connected = Wait-All { param($a, $b, $c) $a.partnerCount -eq 2 -and $b.partnerCount -eq 2 -and $c.partnerCount -eq 2 } 'Three players connected' 20
Write-Output ('Three players connected: ids ' + (($connected | ForEach-Object { $_.myId }) -join ', ') + '.')
& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') -Instances $all | Out-Null
$together = Wait-All { param($a, $b, $c) (Same $a $b) -and (Same $a $c) -and $a.prototypeActors -eq 2 -and $b.prototypeActors -eq 2 -and $c.prototypeActors -eq 2 -and $a.ownsWorld } 'Three players on one floor' 90
Write-Output ('All three on floor ' + $together[0].dungeonDepth + ' with ' + $together[0].monstersTracked + ' matching monsters; everyone sees two partners.')

Hit-Until 'c' { param($a, $b, $c) $a.hitsApplied -gt $together[0].hitsApplied } 'Third player hit landing on the host'
$hit = Wait-All { param($a, $b, $c) (Same $a $b) -and (Same $a $c) -and $a.trackedHp -lt $together[0].trackedHp } 'Monster health after the third player hit'
Write-Output ('Third player hit: health total ' + $together[0].trackedHp + ' -> ' + $hit[0].trackedHp + ' in all three games.')

Send 'test-chat hello from the third player' 'c'
Wait-All { param($a, $b, $c) $a.lastChat -like '*: hello from the third player' -and $b.lastChat -like '*: hello from the third player' } 'Chat reaching both others' 20 | Out-Null
Write-Output 'Chat from the third player reached both others.'

$floorKey = $hit[0].worldKey
Send 'test-invincible on' 'b'
Send 'test-invincible on' 'c'
Send 'town' 'a'
$handover = Wait-All { param($a, $b, $c) $a.gameScene -eq 'Town_Grove' -and !$a.transitioning -and $b.ownsWorld -and $c.worldOwner -eq $b.myId -and $b.worldKey -eq $floorKey -and $c.worldKey -eq $floorKey -and (Same $b $c) } 'Floor handover to the next player' 60
Write-Output ('Host left; player ' + $handover[1].myId + ' took over the floor without a reload, and player ' + $handover[2].myId + ' follows them now.')

Hit-Until 'c' { param($a, $b, $c) $b.hitsApplied -gt $handover[1].hitsApplied } 'Third player hit landing on the new owner'
Wait-All { param($a, $b, $c) (Same $b $c) -and $b.trackedHp -lt $handover[1].trackedHp } 'Monster health after a hit on the new owner' | Out-Null
Write-Output 'Hits now go to the new floor owner.'

Wait-All { param($a, $b, $c) !$a.inCombat } 'Host out of combat' | Out-Null
Send ('follow-partner ' + $handover[1].myId) 'a'
$back = Wait-All { param($a, $b, $c) (Same $a $b) -and (Same $a $c) -and $a.worldOwner -eq $b.myId -and $b.ownsWorld -and $a.prototypeActors -eq 2 } 'Host joining the new owner''s floor' 90
Write-Output ('Host came back and joined player ' + $back[1].myId + '''s floor; all three are together again.')
Send 'test-invincible off' 'b'
Send 'test-invincible off' 'c'

[ordered]@{ passed = $true; together = $together[0].monstersTracked; handoverOwner = $handover[1].myId } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\three-players-verification.json')
Write-Output 'PASS: three players together, hits from any player, chat to all, floor handover without reload, rejoining the new owner.'
