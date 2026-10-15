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

if ((State a).gameScene -eq 'Town_Grove') { Send 'enter-dungeon' }
if ((State b).gameScene -eq 'Town_Grove') { Send 'enter-dungeon' 'b' }
$shared = Wait-Pair { param($a, $b) SameFloor $a $b } 'Shared floor'
Write-Output ('Shared floor ' + $shared.a.dungeonDepth + ': ' + $shared.a.worldMonsters + ' monsters in each world.')

Send 'leave' 'b'
Start-Sleep -Seconds 3
& (Join-Path $PSScriptRoot 'Connect-TwoGames.ps1') | Out-Null
$rejoined = Wait-Pair { param($a, $b) SameFloor $a $b } 'Quick rejoin on the same floor'
Write-Output ('Quick rejoin: still ' + $rejoined.b.worldMonsters + ' monsters on the guest, matching the host. No duplicates.')

Send 'test-resume' 'b'
Send 'next-floor' 'b'
Wait-Pair { param($a, $b) $b.transitioning -or $b.worldKey -ne $a.worldKey } 'Guest leaving by the stairs' 30 | Out-Null
$stairs = Wait-Pair { param($a, $b) (SameFloor $a $b) -and $b.dungeonDepth -eq $a.dungeonDepth } 'Guest stairs leading back to the host floor'
Write-Output 'Guest took the stairs alone and landed back on the host''s floor.'

Send-Until 'town' 'b' { param($a, $b) $b.gameScene -eq 'Town_Grove' } 'Guest walking to town'
Wait-Pair { param($a, $b) $b.gameScene -eq 'Town_Grove' -and !$b.transitioning } 'Guest arriving in town' | Out-Null
Start-Sleep -Seconds 8
if ((State b).gameScene -ne 'Town_Grove') { throw 'Guest was pulled out of town.' }
Write-Output 'Guest stayed in town while the host was in the dungeon.'
Send 'enter-dungeon' 'b'
Wait-Pair { param($a, $b) SameFloor $a $b } 'Guest entering the dungeon onto the host floor' | Out-Null
Write-Output 'Guest entered the dungeon and landed on the host''s floor.'

Send-Until 'town' 'a' { param($a, $b) $a.gameScene -eq 'Town_Grove' } 'Host walking to town'
$released = Wait-Pair { param($a, $b) $a.gameScene -eq 'Town_Grove' -and $b.gameScene -eq 'Dungeon' -and $b.monstersTracked -eq 0 -and !$b.transitioning } 'Host to town, guest released'
Start-Sleep -Seconds 5
if ((State b).gameScene -ne 'Dungeon') { throw 'Guest was pulled to town with the host.' }
Write-Output 'Host went to town; the guest kept the floor to itself.'
Send 'enter-dungeon' 'a'
Wait-Pair { param($a, $b) SameFloor $a $b } 'Guest following the host back down' | Out-Null
Write-Output 'Host came back down; the guest joined the host''s new floor.'

[ordered]@{ passed = $true; shared = $shared; rejoined = $rejoined; stairs = $stairs; released = $released } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\session-flow-verification.json')
Write-Output 'PASS: quick rejoin without duplicates, guest stairs, guest free in town, guest keeps the floor when the host leaves.'
