$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State([string]$Instance) { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 400
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, dungeonDepth, partnerPlates, partnerPortals, partnerTownPortals, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, dungeonDepth, partnerPlates, partnerPortals, partnerTownPortals, status | ConvertTo-Json -Compress))
}

& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') | Out-Null
$floor = Wait-Pair { param($a, $b) $a.partnerPlates -eq 1 -and $b.partnerPlates -eq 1 } 'Name tags over both partners'
Write-Output 'Both players show a game-style name tag over their partner.'
Send 'test-portal'
Wait-Pair { param($a, $b) $b.partnerPortals -eq 1 } 'Host portal showing on the guest''s floor' | Out-Null
Write-Output 'The host''s portal shows up on the guest''s screen.'
Send 'town'
Send 'town' 'b'
Wait-Pair { param($a, $b) $a.gameScene -eq 'Town_Grove' -and $b.gameScene -eq 'Town_Grove' -and !$b.transitioning -and $b.partnerTownPortals -eq 1 } 'Host portal showing in town for the guest' | Out-Null
Write-Output 'The host''s portal shows up in town for the guest.'
Send 'test-resume' 'b'
Send 'test-use-portal' 'b'
$through = Wait-Pair { param($a, $b) $b.gameScene -eq 'Dungeon' -and !$b.transitioning -and $b.dungeonDepth -eq $floor.a.dungeonDepth } 'Guest stepping through the host''s portal'
Write-Output ('Guest stepped through the host''s portal to floor ' + $through.b.dungeonDepth + '.')
Write-Output 'PASS: name tags, portals on the floor and in town, using a friend''s portal.'
