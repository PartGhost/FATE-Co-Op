$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function State([string]$instance) {
    $path = if ($instance -eq 'a') { 'artifacts\runtime\status.json' } else { 'artifacts\runtime-b\status.json' }
    $s = Get-Content -LiteralPath (Join-Path $workspace $path) -Raw | ConvertFrom-Json
    $s | Add-Member combat ($s.combatJson | ConvertFrom-Json)
    return $s
}
function Send([string]$command, [string]$instance='a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $command -Instance $instance }
function Wait-Combat([scriptblock]$condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $a = State a; $b = State b
        if ($a.combat.present -and $b.combat.present -and $a.combat.id -eq $b.combat.id -and $a.combat.hp -eq $b.combat.hp -and (& $condition $a.combat $b.combat)) {
            foreach ($s in @($a,$b)) {
                if ($s.localPetCount -ne 1 -or $s.remotePetActors -ne 1 -or $s.remotePetMeshes -lt 1 -or $s.prototypeActors -ne 1) { throw 'Player or pet duplication/missing actor.' }
            }
            return [pscustomobject]@{a=$a;b=$b}
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Combat did not converge: ' + ($a.combat | ConvertTo-Json -Compress) + ' / ' + ($b.combat | ConvertTo-Json -Compress))
}
Send combat-start
$start = Wait-Combat { param($a,$b) !$a.dead -and $a.hp -eq 80 }
Send test-attack b
$guestHit = Wait-Combat { param($a,$b) $a.hitsB -ge 1 -and $b.hitsB -ge 1 }
Send test-attack a
$bothHit = Wait-Combat { param($a,$b) $a.hitsA -ge 1 -and $b.hitsA -ge 1 }
Start-Sleep -Milliseconds 400
Send 'test-attack pet' b
$petHit = Wait-Combat { param($a,$b) $a.petHits -ge 1 -and $b.petHits -ge 1 }
for ($i=0; $i -lt 12; $i++) {
    $a=State a
    if ($a.combat.dead) { break }
    Send test-attack $(if ($i % 2 -eq 0) { 'a' } else { 'b' })
    Start-Sleep -Milliseconds 500
}
$death = Wait-Combat { param($a,$b) $a.dead -and $b.dead -and $a.deaths -eq 1 -and $b.deaths -eq 1 }
Start-Sleep -Seconds 2
$stableDeath = Wait-Combat { param($a,$b) $a.dead -and $a.deaths -eq 1 -and $b.deaths -eq 1 }
if ($death.a.combat.hitsA -ne $stableDeath.a.combat.hitsA -or $death.a.combat.hitsB -ne $stableDeath.a.combat.hitsB) { throw 'Dead monster accepted additional damage.' }
@{passed=$true;start=$start;guestHit=$guestHit;bothHit=$bothHit;petHit=$petHit;death=$stableDeath} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\shared-combat-verification.json')
Write-Output 'PASS: both players and guest pet contributed; host health converged; one death; one local and remote dog each.'
