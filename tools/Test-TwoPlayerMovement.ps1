$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$started = [DateTime]::UtcNow
function State([string]$Instance) {
    $root = if ($Instance -eq 'a') { 'artifacts\runtime' } else { 'artifacts\runtime-b' }
    Get-Content -LiteralPath (Join-Path $workspace ($root + '\status.json')) -Raw | ConvertFrom-Json
}
$beforeA = State 'a'; $beforeB = State 'b'
if (!$beforeA.networkConnected -or !$beforeB.networkConnected) { throw 'Connect the relay before running the movement test.' }
foreach ($instance in @('a','b')) {
    & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') 'test-resume' -Instance $instance
    $direction = if ($instance -eq 'a') { 'test-walk -1 0' } else { 'test-walk 1 0' }
    & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $direction -Instance $instance
    Start-Sleep -Seconds 5
}
$a = State 'a'; $b = State 'b'
function Distance($u,$v) { [Math]::Sqrt([Math]::Pow($u.x-$v.x,2)+[Math]::Pow($u.z-$v.z,2)) }
$relay = Get-Content -LiteralPath (Join-Path $workspace 'artifacts\pair-result.json') -Raw | ConvertFrom-Json
$report = [ordered]@{
    testedAt = $started.ToString('o')
    primaryTravelA = Distance $beforeA.primaryPosition $a.primaryPosition
    primaryTravelB = Distance $beforeB.primaryPosition $b.primaryPosition
    primaryIdentityPreservedA = $beforeA.primaryId -eq $a.primaryId
    primaryIdentityPreservedB = $beforeB.primaryId -eq $b.primaryId
    guestMatchesPlayerB = (Distance $a.guestPosition $b.primaryPosition) -lt .25
    guestMatchesPlayerA = (Distance $b.guestPosition $a.primaryPosition) -lt .25
    oneGuestEach = $a.prototypeActors -eq 1 -and $b.prototypeActors -eq 1
    relayRequestsRejected = $relay.rejected
}
$report.passed = $report.primaryTravelA -gt 1 -and $report.primaryTravelB -gt 1 -and $report.primaryIdentityPreservedA -and $report.primaryIdentityPreservedB -and $report.guestMatchesPlayerA -and $report.guestMatchesPlayerB -and $report.oneGuestEach -and $relay.rejected -eq 0
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\two-player-verification.json')
$report | ConvertTo-Json
if (!$report.passed) { throw 'Two-player movement verification failed; inspect the measured results.' }
