$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State([string]$Instance = 'a') { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 400
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, dungeonDepth, worldKey, ownsWorld, worldOwner, partnerCount, followPrompt, inCombat, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, dungeonDepth, worldKey, ownsWorld, worldOwner, status | ConvertTo-Json -Compress))
}

& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') | Out-Null
$start = Wait-Pair { param($a, $b) $a.gameScene -eq 'Dungeon' -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey } 'Shared floor' 90
Send 'test-clear 1000'
Send 'test-resume' 'b'
Send 'next-floor' 'b'
Wait-Pair { param($a, $b) $b.dungeonDepth -gt $start.a.dungeonDepth -and !$b.transitioning -and $b.worldKey -ne '' -and $b.ownsWorld } 'Guest on its own next floor' 90 | Out-Null
Wait-Pair { param($a, $b) $a.followPrompt } 'Follow prompt on the host' 60 | Out-Null
Send 'test-answer yes'
$joined = Wait-Pair { param($a, $b) $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and !$a.transitioning -and !$a.ownsWorld } 'Host on the guest''s floor' 90
Write-Output ('The host joined the guest''s floor ' + $joined.a.dungeonDepth + ' (owner ' + $joined.a.worldOwner + ').')

Get-Process FateReawakened -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path (Get-TestGamePath 'b') 'FateReawakened.exe') } | Stop-Process -Force
$deadline = [DateTime]::UtcNow.AddSeconds(25)
do { Start-Sleep -Milliseconds 400; $a = State a } while (!($a.ownsWorld -and $a.partnerCount -eq 0) -and [DateTime]::UtcNow -lt $deadline)
if (!$a.ownsWorld -or $a.partnerCount -ne 0 -or $a.worldKey -ne $joined.a.worldKey) { throw ('The host never took over the floor after its owner dropped: ' + ($a | Select-Object worldKey, ownsWorld, worldOwner, partnerCount, status | ConvertTo-Json -Compress)) }
Write-Output 'PASS: when the floor owner drops out, the player left behind takes over the floor without reloading.'
