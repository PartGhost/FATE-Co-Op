$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State([string]$Instance = 'a') { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Usable($State, [string]$Kind) { if ($State.fixtures -match ($Kind + '=(\d+)/(\d+)')) { [int]$Matches[1] } else { -1 } }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 300
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, template, worldKey, fixtures, gold, partnerIcon, partnerPetPortrait, replicaImpacts, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, template, worldKey, fixtures, gold, partnerIcon, partnerPetPortrait, replicaImpacts, status | ConvertTo-Json -Compress))
}

& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') | Out-Null
Send 'test-template Volcano_01' 'a'
while ((State a).template -ne 'Volcano_01') {
    $depth = (State a).dungeonDepth
    Send 'test-resume' 'a'
    Send 'next-floor' 'a'
    Wait-Pair { param($a, $b) $a.dungeonDepth -gt $depth -and !$a.transitioning -and $a.worldKey -ne '' } 'Host on the next floor' 90 | Out-Null
}
if ((State a).worldKey -ne (State b).worldKey) {
    Wait-Pair { param($a, $b) $b.partnerElsewhere } 'Guest told the host left' 30 | Out-Null
    Send 'test-clear 25' 'b'
    Wait-Pair { param($a, $b) !$b.inCombat } 'Guest out of combat' 30 | Out-Null
    Send ('follow-partner ' + (State a).myId) 'b'
}
$start = Wait-Pair { param($a, $b) $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and !$b.transitioning -and $b.fixtures -ne '' -and $a.fixtures -eq $b.fixtures } 'Guest on the same floor with the same fixtures' 90
Send 'test-resume' 'a'
Send 'test-resume' 'b'
Write-Output ('Both players see ' + $start.a.fixtures + ' on the ' + $start.a.template + ' floor.')

Send 'test-fixture Crystal' 'b'
$broken = Wait-Pair { param($a, $b) (Usable $a 'Crystal') -eq (Usable $start.a 'Crystal') - 1 -and (Usable $b 'Crystal') -eq (Usable $start.b 'Crystal') - 1 } 'A crystal the guest broke'
Write-Output ('The guest broke a crystal on both screens: ' + $broken.a.fixtures)

Send 'test-fixture GoldCart' 'b'
$opened = Wait-Pair { param($a, $b) (Usable $a 'GoldCart') -eq (Usable $start.a 'GoldCart') - 1 -and (Usable $b 'GoldCart') -eq (Usable $start.b 'GoldCart') - 1 -and ($b.gold -gt $start.b.gold -or $b.groundItems -gt $start.b.groundItems) } 'A gold cart the guest opened'
Write-Output ('The guest emptied a gold cart on both screens; its gold is on the floor or in the guest''s purse (+' + ($opened.b.gold - $start.b.gold) + ').')

if ((Usable $start.b 'Campfire') -gt 0) {
    Send 'test-vitals 20' 'b'
    Send 'test-fixture Campfire' 'b'
    $rested = Wait-Pair { param($a, $b) (Usable $b 'Campfire') -eq (Usable $start.b 'Campfire') - 1 -and [int]($b.vitals.Split('/')[0]) -ge [int]($b.vitals.Split('/')[1].Split(' ')[0]) * 0.65 } 'The guest resting at a campfire'
    Write-Output ('The guest rested at a campfire from 20% to ' + $rested.b.vitals + '.')
}

$icons = Wait-Pair { param($a, $b) $a.partnerIcon -ne '' -and $b.partnerIcon -ne '' -and $b.partnerPetPortrait -ne '' } 'Friend icons and pet portraits'
if ($icons.a.partnerIcon -match 'Partyring' -or $icons.a.partnerIcon.Split(' ')[0] -ne $icons.b.partnerIcon.Split(' ')[0] -or $icons.a.partnerIcon -eq $icons.b.partnerIcon) { throw ('Friend map icons are wrong: A=' + $icons.a.partnerIcon + ' B=' + $icons.b.partnerIcon) }
Write-Output ('Friends show on the map as ' + $icons.a.partnerIcon.Split(' ')[0] + ' in their own color (A sees ' + $icons.a.partnerIcon.Split(' ')[1] + ', B sees ' + $icons.b.partnerIcon.Split(' ')[1] + ').')

if ($icons.b.partnerPetPortrait -notmatch '^ProfilePic-') { throw ('The host pet has no portrait on the guest: ' + $icons.b.partnerPetPortrait) }
Write-Output ('The guest shows the host pet portrait ' + $icons.b.partnerPetPortrait + '.')

$before = Wait-Pair { param($a, $b) $true } 'State'
Send 'test-near-partner' 'b'
Send 'test-party-heal' 'b'
$healed = Wait-Pair { param($a, $b) $b.replicaImpacts -gt $before.b.replicaImpacts -and $a.friendImpacts -gt $before.a.friendImpacts } 'Party heal impact on the host stand-in for the caster'
Write-Output ('The caster sees the party heal land on the friend (' + ($healed.b.replicaImpacts - $before.b.replicaImpacts) + ' impact) and the friend sees it on themselves.')
