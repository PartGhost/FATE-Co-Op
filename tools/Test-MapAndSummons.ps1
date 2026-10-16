$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State([string]$Instance = 'a') { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 300
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, worldKey, mapOpenCells, localSummons, partnerSummons, monstersTracked, friendImpacts, dungeonFloors, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, worldKey, mapOpenCells, localSummons, partnerSummons, monstersTracked, friendImpacts, dungeonFloors, status | ConvertTo-Json -Compress))
}

& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') | Out-Null
$start = Wait-Pair { param($a, $b) $a.gameScene -eq 'Dungeon' -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.guestId -ne 0 -and $b.guestId -ne 0 } 'Shared floor' 90
Send 'test-resume' 'a'
Send 'test-resume' 'b'
Send 'test-invincible on' 'a'
Send 'test-invincible on' 'b'

$mapped = $null
foreach ($direction in '1 0', '0 1', '-1 0', '0 -1') {
    Send ('test-move ' + $direction + ' 6') 'b'
    try { $mapped = Wait-Pair { param($a, $b) $a.mapOpenCells -gt $start.a.mapOpenCells } 'Guest exploring for the host' 8; break } catch { }
}
if (!$mapped) { throw 'The guest walked four ways without opening any of the host''s map.' }
Write-Output ('The guest walking opened ' + ($mapped.a.mapOpenCells - $start.a.mapOpenCells) + ' map cells for the host.')

$before = Wait-Pair { param($a, $b) $true } 'State'
Send 'test-summon' 'b'
$summoned = Wait-Pair { param($a, $b) $b.localSummons -gt 0 -and $a.partnerSummons -gt 0 } 'Guest summon on the host'
if ($summoned.a.monstersTracked -gt $before.a.monstersTracked) { throw 'The guest summon became a monster on the host.' }
Send 'test-summon' 'a'
$hostSummon = Wait-Pair { param($a, $b) $a.localSummons -gt 0 -and $b.partnerSummons -gt 0 } 'Host summon on the guest'
if ($hostSummon.b.monstersTracked -gt $summoned.b.monstersTracked) { throw 'The host summon became a monster on the guest.' }
Write-Output ('Summons show as friendly copies: host sees ' + $hostSummon.a.partnerSummons + ', guest sees ' + $hostSummon.b.partnerSummons + ', no new monsters.')

Send 'test-unsummon' 'a'
Send 'test-unsummon' 'b'
Wait-Pair { param($a, $b) $a.localSummons -eq 0 -and $b.localSummons -eq 0 -and $a.partnerSummons -eq 0 -and $b.partnerSummons -eq 0 } 'Summons dismissed' | Out-Null
Send 'test-shift-heal' 'b'
Wait-Pair { param($a, $b) $a.friendImpacts -gt $before.a.friendImpacts } 'Heal effect on the host' 15 | Out-Null
Write-Output 'The guest heal played its effect on the host.'

Send 'town' 'a'
Send 'town' 'b'
$town = Wait-Pair { param($a, $b) $a.gameScene -eq 'Town_Grove' -and $b.gameScene -eq 'Town_Grove' -and !$a.transitioning -and !$b.transitioning -and $a.townNpcs -like '*SullivantheReaper*' } 'Both in town' 90
Send 'test-npc SullivantheReaper yes'
Wait-Pair { param($a, $b) $b.status -like '*reset the dungeons*' -and $a.status -like '*dungeons were reset*' } 'Dungeon reset reaching the guest' | Out-Null
Write-Output ('Dungeon reset: host floors ' + $town.a.dungeonFloors + ' -> ' + (State a).dungeonFloors + ', guest floors ' + $town.b.dungeonFloors + ' -> ' + (State b).dungeonFloors + '.')
Write-Output 'PASS: friends reveal the map for each other, summons stay friendly copies, friend heals show on you, and Sullivan resets dungeons for everyone.'
