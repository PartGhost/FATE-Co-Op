$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function Read-State([string]$Instance) {
    $runtime = if ($Instance -eq 'a') { 'artifacts\runtime' } else { 'artifacts\runtime-b' }
    Get-Content -LiteralPath (Join-Path $workspace ($runtime + '\status.json')) -Raw | ConvertFrom-Json
}
function Wait-Pair([int]$Depth) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 400
        try { $a = Read-State 'a'; $b = Read-State 'b' } catch { continue }
        if (!$a.transitioning -and !$b.transitioning -and $a.primaryId -ne 0 -and $b.primaryId -ne 0 -and $a.dungeonDepth -eq $Depth -and $b.dungeonDepth -eq $Depth -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.guestId -ne 0 -and $b.guestId -ne 0 -and $a.prototypeActors -eq 1 -and $b.prototypeActors -eq 1) {
            if ($a.remotePetActors -ne 1 -or $b.remotePetActors -ne 1) { continue }
            if ($a.localPetCount -ne 1 -or $b.localPetCount -ne 1) { throw 'Local pet ownership changed.' }
            if ($Depth -gt 0 -and ($a.layoutHash -eq '' -or $a.layoutHash -ne $b.layoutHash)) { throw 'Dungeon fingerprints differ.' }
            return [pscustomobject]@{ depth=$Depth; layout=$a.layoutHash; worldKey=$a.worldKey; a=$a; b=$b }
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Shared floor timed out: A=' + ($a | ConvertTo-Json -Compress) + ' B=' + ($b | ConvertTo-Json -Compress))
}
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
$a = Read-State 'a'
if ($a.gameScene -eq 'Town_Grove') { Send 'enter-dungeon' }
if ((Read-State 'b').gameScene -eq 'Town_Grove') { Send 'enter-dungeon' 'b' }
$floor1 = Wait-Pair 1
Write-Output 'Floor 1: matching maze cells, stairs, world identity and one guest each.'
$movement = @()
foreach ($instance in @('a','b')) {
    $before = Read-State $instance
    $travel = 0
    foreach ($direction in @('1 0','0 1','-1 0','0 -1')) {
        Send 'test-resume' $instance
        Send ('test-walk ' + $direction) $instance
        Start-Sleep -Seconds 4
        $after = Read-State $instance
        if ($after.primaryId -ne $before.primaryId) { throw 'Primary identity changed during movement on one floor.' }
        $travel = [Math]::Sqrt([Math]::Pow($after.primaryPosition.x-$before.primaryPosition.x,2)+[Math]::Pow($after.primaryPosition.z-$before.primaryPosition.z,2))
        if ($travel -gt 1) { break }
    }
    if ($travel -le 1) { throw ('No measurable navigation movement for ' + $instance) }
    $movement += [pscustomobject]@{ instance=$instance; travel=$travel; primaryIdentityPreserved=$true }
}
Start-Sleep -Seconds 2
$settled = Wait-Pair 1
function Distance($u,$v) { [Math]::Sqrt([Math]::Pow($u.x-$v.x,2)+[Math]::Pow($u.z-$v.z,2)) }
if ((Distance $settled.a.guestPosition $settled.b.primaryPosition) -gt .25 -or (Distance $settled.b.guestPosition $settled.a.primaryPosition) -gt .25) { throw 'Replicas did not converge after movement.' }
Write-Output 'Independent movement and converged replicas passed on floor 1.'
Send 'next-floor'
$floor2 = Wait-Pair 2
Write-Output 'Floor 2: joining instance followed the host and matched the layout.'
Send 'next-floor'
$floor3 = Wait-Pair 3
Write-Output 'Floor 3: matching layout after another host floor transition.'
Send 'town'
Send 'town' 'b'
$town = Wait-Pair 0
Write-Output 'Both players returned to Grove and resumed replication.'
[ordered]@{ floor1=$floor1; movement=$movement; settled=$settled; floor2=$floor2; floor3=$floor3; returnedToGrove=$town; passed=$true } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\shared-dungeon-verification.json')
