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
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, worldKey, downed, replicaDown, reviveProgress, primaryAlive, primaryHp, fateChoice, chatReceived, lastChat, inCombat, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, worldKey, downed, replicaDown, reviveProgress, primaryAlive, primaryHp, fateChoice, chatReceived, lastChat, inCombat, status | ConvertTo-Json -Compress))
}

if ((State a).gameScene -eq 'Town_Grove') { Send 'enter-dungeon' }
if ((State b).gameScene -eq 'Town_Grove') { Send 'enter-dungeon' 'b' }
$start = Wait-Pair { param($a, $b) $a.gameScene -eq 'Dungeon' -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.guestId -ne 0 -and $b.guestId -ne 0 } 'Shared floor' 90
Send 'test-resume' 'a'
Send 'test-resume' 'b'

Send 'test-chat hello from the host'
Wait-Pair { param($a, $b) $b.chatReceived -gt $start.b.chatReceived -and $b.lastChat -like '*: hello from the host' } 'Host chat on the guest' | Out-Null
Send 'test-chat and back from the guest' 'b'
Wait-Pair { param($a, $b) $a.chatReceived -gt $start.a.chatReceived -and $a.lastChat -like '*: and back from the guest' } 'Guest chat on the host' | Out-Null
Write-Output 'Chat went both ways.'

Send 'test-down' 'b'
Wait-Pair { param($a, $b) $b.downed -and $b.primaryHp -le 0 -and !$b.fateChoice -and $a.replicaDown -and $b.worldKey -eq $a.worldKey } 'Guest going down' | Out-Null
Write-Output 'Guest went down instead of dying; the host sees them lying on the floor.'
Send 'test-near-partner'
$reviving = Wait-Pair { param($a, $b) $a.reviveProgress -gt 0.4 -and $b.downed } 'Host reviving' 10
Send 'test-swing'
$interrupted = Wait-Pair { param($a, $b) $a.reviveProgress -lt $reviving.a.reviveProgress -and $b.downed } 'Swing resetting the revive' 5
Write-Output ('A swing reset the revive from ' + [Math]::Round($reviving.a.reviveProgress, 2) + ' to ' + [Math]::Round($interrupted.a.reviveProgress, 2) + '.')
$revived = Wait-Pair { param($a, $b) !$b.downed -and $b.primaryAlive -and $b.primaryHp -gt 0 -and !$a.replicaDown -and !$b.fateChoice } 'Guest revived' 25
Write-Output ('Guest revived at ' + [Math]::Round($revived.b.primaryHp) + ' HP.')

Send 'test-move 1 0 3'
Start-Sleep -Seconds 4
Send 'test-down' 'b'
Wait-Pair { param($a, $b) $b.downed } 'Guest going down again' | Out-Null
Start-Sleep -Seconds 2
if ((State a).reviveProgress -gt 0) { throw 'Host revived from too far away.' }
$died = Wait-Pair { param($a, $b) !$b.downed -and $b.fateChoice } 'Guest bleeding out' 45
Write-Output 'Left alone, the guest bled out into the normal death screen.'
Send 'test-fate-town' 'b'
Wait-Pair { param($a, $b) $b.gameScene -eq 'Town_Grove' -and !$b.transitioning -and $b.primaryAlive } 'Guest back in town after death' 60 | Out-Null
Send 'follow-partner' 'b'
Wait-Pair { param($a, $b) $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and !$a.followPrompt } 'Guest returning to the host' 90 | Out-Null
Write-Output 'Guest went back to the host with Go to partner.'

Send 'test-resume' 'b'
Send 'test-move -1 0 3'
Start-Sleep -Seconds 4
Send 'test-down' 'b'
Wait-Pair { param($a, $b) $b.downed } 'Guest going down before quitting' | Out-Null
Send 'test-pause' 'b'
Start-Sleep -Seconds 1
Send 'test-save-quit' 'b'
Wait-Pair { param($a, $b) !$b.downed -and $b.fateChoice -and $b.gameScene -eq 'Dungeon' } 'Save and quit while down counting as death' | Out-Null
Write-Output 'Save and Quit while down ran the real death instead of quitting.'
Send 'test-fate-town' 'b'
Wait-Pair { param($a, $b) $b.gameScene -eq 'Town_Grove' -and !$b.transitioning -and $b.primaryAlive } 'Guest back in town after the quit death' 60 | Out-Null

[ordered]@{ passed = $true; reviving = $reviving.a.reviveProgress; interrupted = $interrupted.a.reviveProgress; revivedHp = $revived.b.primaryHp } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\revive-chat-verification.json')
Write-Output 'PASS: chat, downed state, revive with interruption, bleed-out, quitting while down.'
