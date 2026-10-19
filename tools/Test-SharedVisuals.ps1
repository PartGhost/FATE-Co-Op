$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function State([string]$Instance) {
    $path = if ($Instance -eq 'a') { 'artifacts\runtime\status.json' } else { 'artifacts\runtime-b\status.json' }
    Get-Content -LiteralPath (Join-Path $workspace $path) -Raw | ConvertFrom-Json
}
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 300
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object localWeaponParticles, replicaWeaponParticles, remoteEffects, mirrorEffects, damagePopups, spellsReplayed, arrowsDrawn, levelUpsPlayed, localFishing, replicaFishing, networkRejected, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object localWeaponParticles, replicaWeaponParticles, remoteEffects, mirrorEffects, damagePopups, spellsReplayed, arrowsDrawn, levelUpsPlayed, localFishing, replicaFishing, networkRejected, status | ConvertTo-Json -Compress))
}

& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') | Out-Null
$start =Wait-Pair { param($a, $b) $a.gameScene -eq 'Dungeon' -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.guestId -ne 0 -and $b.guestId -ne 0 -and $b.monstersTracked -gt 0 } 'Shared floor' 90
do {
    Send 'test-resume' 'a'
    Send 'next-floor'
    Wait-Pair { param($a, $b) $b.followPrompt } 'Follow prompt on the guest' 60 | Out-Null
    Send 'follow-partner' 'b'
    $start = Wait-Pair { param($a, $b) $a.dungeonDepth -gt $start.a.dungeonDepth -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.guestId -ne 0 -and $b.guestId -ne 0 -and $b.monstersTracked -gt 0 } 'Fresh shared floor' 120
} while ($start.a.monstersTracked - $start.a.trackedDead -lt 5)
Send 'test-resume' 'a'
Send 'test-resume' 'b'

Send 'test-glow'
$glow = Wait-Pair { param($a, $b) $a.localWeaponParticles -gt 0 -and $b.replicaWeaponParticles -eq $a.localWeaponParticles } 'Weapon glow on the host''s replica'
Write-Output ('Weapon glow: the host''s weapon has ' + $glow.a.localWeaponParticles + ' particle systems; the guest''s replica of it shows ' + $glow.b.replicaWeaponParticles + '.')

Send 'test-aura'
Wait-Pair { param($a, $b) $b.remoteEffects -match 'Weapon_Fire' } 'Player effect particles on the replica' | Out-Null
Send 'test-aura monster'
Wait-Pair { param($a, $b) $b.mirrorEffects -gt $start.b.mirrorEffects } 'Monster effect particles on the guest' | Out-Null
Write-Output 'Effect particles: the host''s own aura shows on its replica, and a monster''s aura shows on the guest''s copy.'

for ($i = 0; $i -lt 60 -and (State b).damagePopups -le $start.b.damagePopups; $i++) { Send 'test-hit' 'b'; Start-Sleep -Milliseconds 300 }
Wait-Pair { param($a, $b) $b.damagePopups -gt $start.b.damagePopups } 'Damage numbers on the guest' | Out-Null
Write-Output 'Damage numbers: the guest sees numbers on monsters it hits.'

Send 'test-cast' 'a'
Wait-Pair { param($a, $b) $b.spellsReplayed -gt $start.b.spellsReplayed } 'Host spell on the guest''s screen' | Out-Null
Send 'test-cast' 'b'
Wait-Pair { param($a, $b) $a.spellsReplayed -gt $start.a.spellsReplayed } 'Guest spell on the host''s screen' | Out-Null
Write-Output 'Spells: each player''s spell plays on the other screen.'

Send 'test-arrow' 'a'
Wait-Pair { param($a, $b) $b.arrowsDrawn -gt $start.b.arrowsDrawn } 'Arrow trail on the guest' | Out-Null
Send 'test-levelup' 'a'
Wait-Pair { param($a, $b) $b.levelUpsPlayed -gt $start.b.levelUpsPlayed } 'Level-up sparkle on the guest' | Out-Null
Write-Output 'Arrow trail and level-up sparkle show on the partner''s screen.'

Send 'test-fish' 'a'
Send 'test-fish' 'b'
$fishing = Wait-Pair { param($a, $b) ($a.localFishing -band 1) -and ($b.localFishing -band 1) -and ($a.replicaFishing -band 1) -and ($b.replicaFishing -band 1) } 'Both players fishing'
Write-Output 'Fishing: both players fish at the same time and each sees the other fishing.'
Send 'test-fish off' 'a'
Send 'test-fish off' 'b'
$stopped = Wait-Pair { param($a, $b) !($a.replicaFishing -band 1) -and !($b.replicaFishing -band 1) } 'Fishing stopping on both replicas'
if ($stopped.a.networkRejected -ne $start.a.networkRejected -or $stopped.b.networkRejected -ne $start.b.networkRejected) { throw 'Peer messages were rejected during the visuals test.' }

[ordered]@{ passed = $true; glow = $glow; fishing = $fishing; stopped = $stopped } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\shared-visuals-verification.json')
Write-Output 'PASS: weapon glow, effect particles, damage numbers, spells, arrow trails, level-ups and fishing show for both players.'
