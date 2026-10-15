$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function State([string]$Instance) {
    $path = if ($Instance -eq 'a') { 'artifacts\runtime\status.json' } else { 'artifacts\runtime-b\status.json' }
    Get-Content -LiteralPath (Join-Path $workspace $path) -Raw | ConvertFrom-Json
}
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 300
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object monstersTracked, trackedHp, trackedDead, hitsApplied, killsSent, strikesSent, swingsPlayed, primaryExperience | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object monstersTracked, trackedHp, trackedDead, hitsSent, killsTaken, strikesTaken, swingsPlayed, primaryExperience | ConvertTo-Json -Compress))
}
function Invoke-HitUntil([string]$Instance, [scriptblock]$Done, [string]$What) {
    for ($i = 0; $i -lt 300; $i++) {
        $a = State a; $b = State b
        if (& $Done $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
        Send 'test-hit' $Instance
        Start-Sleep -Milliseconds 150
    }
    throw ($What + ' never happened.')
}
function Converged($a, $b) { $a.monstersTracked -gt 0 -and $b.monstersTracked -eq $a.monstersTracked -and [Math]::Abs($a.trackedHp - $b.trackedHp) -le 1 -and $a.trackedDead -eq $b.trackedDead }

$start = State a
if ($start.gameScene -eq 'Town_Grove') { Send 'enter-dungeon' }
$paired = Wait-Pair { param($a, $b) $a.dungeonDepth -ge 1 -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.guestId -ne 0 -and $b.guestId -ne 0 -and (Converged $a $b) } 'Shared floor with matching monsters' 90
Send 'test-resume' 'a'
Send 'test-resume' 'b'
if ($paired.a.remoteName -eq '' -or $paired.a.remoteName -eq 'Coop Test Guest' -or $paired.b.remoteName -eq '' -or $paired.b.remoteName -eq 'Coop Test Guest') { throw 'Partner replicas do not carry the Steam names.' }
Write-Output ('Paired: ' + $paired.a.monstersTracked + ' monsters match, health total ' + $paired.a.trackedHp + '. Names: A sees ' + $paired.a.remoteName + ', B sees ' + $paired.b.remoteName + '.')

Invoke-HitUntil 'b' { param($a, $b) $a.hitsApplied -gt $paired.a.hitsApplied } 'Guest hit landing on the host' | Out-Null
$hitConverged = Wait-Pair { param($a, $b) (Converged $a $b) -and $a.trackedHp -lt $paired.a.trackedHp } 'Monster health after the guest hit'
Write-Output ('Guest hit landed: health total ' + $paired.a.trackedHp + ' -> ' + $hitConverged.a.trackedHp + ' in both games.')

Invoke-HitUntil 'b' { param($a, $b) $a.killsSent -gt $paired.a.killsSent } 'Guest kill' | Out-Null
$guestKillSeen = Wait-Pair { param($a, $b) $b.killsTaken -gt $paired.b.killsTaken -and $b.primaryExperience -gt $paired.b.primaryExperience -and $a.primaryExperience -gt $paired.a.primaryExperience -and (Converged $a $b) } 'XP after the guest kill'
Write-Output ('Guest kill: both players gained XP (A ' + $paired.a.primaryExperience + ' -> ' + $guestKillSeen.a.primaryExperience + ', B ' + $paired.b.primaryExperience + ' -> ' + $guestKillSeen.b.primaryExperience + ').')

Invoke-HitUntil 'a' { param($a, $b) $a.killsSent -gt $guestKillSeen.a.killsSent } 'Host kill' | Out-Null
$hostKillSeen = Wait-Pair { param($a, $b) $b.killsTaken -gt $guestKillSeen.b.killsTaken -and $b.primaryExperience -gt $guestKillSeen.b.primaryExperience -and (Converged $a $b) } 'XP after the host kill'
Write-Output ('Host kill: guest gained XP too (B ' + $guestKillSeen.b.primaryExperience + ' -> ' + $hostKillSeen.b.primaryExperience + ').')

$strike = $null
for ($i = 0; $i -lt 20 -and !$strike; $i++) {
    Send 'test-aggro'
    try { $strike = Wait-Pair { param($a, $b) $a.strikesSent -gt $hostKillSeen.a.strikesSent -and $b.strikesTaken -gt $hostKillSeen.b.strikesTaken } 'Monster attack on the guest' 3 } catch { }
}
Send 'test-calm'
if (!$strike) { throw ('Monster attack on the guest never happened: ' + (State a).aggro) }
Write-Output ('Monster attacked the guest replica; the guest rolled it against its own armor (guest HP ' + $hostKillSeen.b.primaryHp + ' -> ' + $strike.b.primaryHp + ').')

Send 'test-swing' 'a'
$swingB = Wait-Pair { param($a, $b) $b.swingsPlayed -gt $strike.b.swingsPlayed } 'Host swing on the guest replica'
Send 'test-swing' 'b'
$swingA = Wait-Pair { param($a, $b) $a.swingsPlayed -gt $strike.a.swingsPlayed } 'Guest swing on the host replica'
$missingClips = @(foreach ($log in @('artifacts\runtime\Player.log', 'artifacts\runtime-b\Player.log')) { Select-String -LiteralPath (Join-Path $workspace $log) -Pattern 'No animation clip found for: .* in FateCoop_Prototype_Guest' -ErrorAction SilentlyContinue })
if ($missingClips.Count -gt 0) { throw ('Replica attack clips missing: ' + $missingClips[0].Line) }
Write-Output 'Both replicas played their partner''s attack swing with a real clip.'

[ordered]@{ passed = $true; paired = $paired; guestHit = $hitConverged; guestKill = $guestKillSeen; hostKill = $hostKillSeen; strike = $strike; swingOnB = $swingB; swingOnA = $swingA } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\shared-combat-verification.json')
Write-Output 'PASS: shared monsters, guest and host kills with XP for both, monster strikes on the guest, replica swings.'
