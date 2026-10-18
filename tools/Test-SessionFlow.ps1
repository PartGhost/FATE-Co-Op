$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function State([string]$Instance) {
    $path = if ($Instance -eq 'a') { 'artifacts\runtime\status.json' } else { 'artifacts\runtime-b\status.json' }
    Get-Content -LiteralPath (Join-Path $workspace $path) -Raw | ConvertFrom-Json
}
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 400
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, dungeonDepth, worldKey, sessionRole, networkConnected, monstersTracked, worldMonsters, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, dungeonDepth, worldKey, sessionRole, networkConnected, monstersTracked, worldMonsters, status | ConvertTo-Json -Compress))
}
function Send-Until([string]$Command, [string]$Instance, [scriptblock]$Done, [string]$What) {
    for ($i = 0; $i -lt 30; $i++) {
        Send $Command $Instance
        Start-Sleep -Seconds 2
        if (& $Done (State a) (State b)) { return }
    }
    throw ($What + ' never happened.')
}
function SameFloor($a, $b) { $a.gameScene -eq 'Dungeon' -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $b.monstersTracked -gt 0 -and $b.monstersTracked -eq $a.monstersTracked -and $b.worldMonsters -eq $a.worldMonsters }

if ((State a).gameScene -eq 'Town_Grove') {
    Send 'enter-dungeon'
    Wait-Pair { param($a, $b) $a.gameScene -eq 'Dungeon' -and !$a.transitioning -and $a.worldKey -ne '' } 'Host on floor 1' | Out-Null
    Send 'test-invincible on' 'a'
}
if ((State b).gameScene -eq 'Town_Grove') { Send 'enter-dungeon' 'b' }
if ((State a).worldKey -ne (State b).worldKey) {
    Wait-Pair { param($a, $b) !$b.inCombat -and !$b.transitioning } 'Guest out of combat' 30 | Out-Null
    Send 'follow-partner' 'b'
}
$shared = Wait-Pair { param($a, $b) SameFloor $a $b } 'Shared floor'
Send 'test-invincible on' 'a'
Send 'test-invincible on' 'b'
Write-Output ('Shared floor ' + $shared.a.dungeonDepth + ': ' + $shared.a.worldMonsters + ' monsters in each world.')

Send 'leave' 'b'
Start-Sleep -Seconds 3
& (Join-Path $PSScriptRoot 'Connect-Games.ps1') | Out-Null
$rejoined = Wait-Pair { param($a, $b) SameFloor $a $b } 'Quick rejoin on the same floor'
Write-Output ('Quick rejoin: still ' + $rejoined.b.worldMonsters + ' monsters on the guest, matching the host. No duplicates.')

Send 'test-resume' 'b'
Send 'next-floor' 'b'
$stairs = Wait-Pair { param($a, $b) !$b.transitioning -and $b.gameScene -eq 'Dungeon' -and $b.dungeonDepth -eq $a.dungeonDepth + 1 -and $b.worldKey -ne '' -and $b.worldKey -ne $a.worldKey -and $a.followPrompt } 'Guest alone on the next floor, host asked to follow'
Start-Sleep -Seconds 5
if ((State b).dungeonDepth -ne $stairs.a.dungeonDepth + 1) { throw 'Guest was pulled back to the host floor.' }
Write-Output 'Guest took the stairs alone and kept its own floor; the host got a follow prompt.'
Wait-Pair { param($a, $b) !$a.inCombat } 'Host out of combat' 30 | Out-Null
Send 'follow-partner' 'a'
Wait-Pair { param($a, $b) (SameFloor $a $b) -and $a.dungeonDepth -eq $stairs.b.dungeonDepth -and !$a.followPrompt } 'Host following the guest down' | Out-Null
Write-Output 'Host said yes and both met on the guest''s depth.'

Send 'test-clear 25' 'b'
Send-Until 'town' 'b' { param($a, $b) $b.gameScene -eq 'Town_Grove' } 'Guest walking to town'
Wait-Pair { param($a, $b) $b.gameScene -eq 'Town_Grove' -and !$b.transitioning -and $a.followPrompt -and $b.partnerElsewhere } 'Guest in town, host asked to follow' | Out-Null
Start-Sleep -Seconds 8
if ((State b).gameScene -ne 'Town_Grove') { throw 'Guest was pulled out of town.' }
Write-Output 'Guest stayed in town while the host was in the dungeon.'
Wait-Pair { param($a, $b) !$b.inCombat } 'Guest out of combat' 30 | Out-Null
Send 'follow-partner' 'b'
Wait-Pair { param($a, $b) (SameFloor $a $b) -and !$a.followPrompt } 'Guest going to the host from town' | Out-Null
Write-Output 'Go to partner took the guest from town onto the host''s floor, and the host''s open prompt closed itself.'

Send-Until 'town' 'a' { param($a, $b) $a.gameScene -eq 'Town_Grove' } 'Host walking to town'
$released = Wait-Pair { param($a, $b) $a.gameScene -eq 'Town_Grove' -and $b.gameScene -eq 'Dungeon' -and $b.ownsWorld -and !$b.transitioning -and $b.followPrompt } 'Host to town, guest released and asked to follow'
Start-Sleep -Seconds 5
if ((State b).gameScene -ne 'Dungeon') { throw 'Guest was pulled to town with the host.' }
Write-Output 'Host went to town; the guest took over the floor.'
Send 'test-swing' 'a'
Start-Sleep -Seconds 1
Send 'follow-partner' 'a'
Start-Sleep -Seconds 4
$blocked = State a
if (!$blocked.inCombat -or $blocked.gameScene -ne 'Town_Grove') { throw 'Go to partner worked during a fight.' }
Write-Output 'Go to partner is refused for 8 seconds after a swing.'
Wait-Pair { param($a, $b) !$a.inCombat } 'Host leaving combat' 20 | Out-Null
Send 'follow-partner' 'a'
Wait-Pair { param($a, $b) (SameFloor $a $b) -and !$b.followPrompt } 'Host going back down to the guest' | Out-Null
Write-Output 'Host went to the guest''s depth; the guest joined the host''s version of the floor.'

[ordered]@{ passed = $true; shared = $shared; rejoined = $rejoined; stairs = $stairs; released = $released } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\session-flow-verification.json')
Write-Output 'PASS: quick rejoin, independent floors, follow prompts, go to partner, combat lockout.'
