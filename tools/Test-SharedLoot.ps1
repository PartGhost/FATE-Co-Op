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
    throw ($What + ' timed out: A=' + ($a | Select-Object groundItems, bagItems, lootGiven, openChests, brokenBarrels | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object groundItems, bagItems, lootTaken, dropsSent, openChests, brokenBarrels | ConvertTo-Json -Compress))
}

$start = State a
& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') | Out-Null
$paired = Wait-Pair { param($a, $b) $a.dungeonDepth -ge 1 -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.guestId -ne 0 -and $b.guestId -ne 0 } 'Shared floor' 90
Send 'test-resume' 'a'
Send 'test-resume' 'b'
$base = $null
for ($i = 0; $i -lt 20 -and !$base; $i++) {
    $first = Wait-Pair { param($a, $b) $a.groundItems -eq $b.groundItems } 'Matching ground items'
    Start-Sleep -Seconds 2
    $second = Wait-Pair { param($a, $b) $a.groundItems -eq $b.groundItems } 'Matching ground items'
    if ($second.a.groundItems -eq $first.a.groundItems -and $second.a.bagItems -eq $first.a.bagItems -and $second.b.bagItems -eq $first.b.bagItems) { $base = $second }
}
if (!$base) { throw 'Ground items never settled.' }
$ground = $base.a.groundItems
$bags = $base.a.bagItems + $base.b.bagItems
Write-Output ('Paired: ' + $ground + ' items and gold piles on the ground in both games.')

Send 'test-drop' 'b'
$guestDrop = Wait-Pair { param($a, $b) $a.groundItems -eq $ground + 1 -and $b.groundItems -eq $ground + 1 -and $b.bagItems -eq $base.b.bagItems - 1 } 'Guest drop showing for both'
Write-Output 'Guest dropped an item; it appeared on the host''s ground and back on the guest''s.'

Send 'test-take' 'a'
$hostTake = Wait-Pair { param($a, $b) $a.groundItems -eq $ground -and $b.groundItems -eq $ground -and $a.bagItems -eq $base.a.bagItems + 1 } 'Host pickup'
Write-Output 'Host picked it up; it vanished from both games.'

Send 'test-drop' 'a'
$hostDrop = Wait-Pair { param($a, $b) $a.groundItems -eq $ground + 1 -and $b.groundItems -eq $ground + 1 -and $a.bagItems -eq $base.a.bagItems } 'Host drop showing for both'
Send 'test-take' 'b'
$guestTake = Wait-Pair { param($a, $b) $a.groundItems -eq $ground -and $b.groundItems -eq $ground -and $b.lootTaken -gt $base.b.lootTaken -and $b.bagItems -eq $base.b.bagItems } 'Guest pickup'
if ($guestTake.a.bagItems + $guestTake.b.bagItems -ne $bags) { throw 'Item count changed across both bags: duplication or loss.' }
Write-Output 'Host dropped it back; the guest picked it up. Both bags together hold the same number of items as at the start.'

Send 'test-open' 'b'
$chest = Wait-Pair { param($a, $b) $a.openChests -gt $base.a.openChests -and $b.openChests -eq $a.openChests } 'Guest chest open'
Write-Output ('Guest opened a chest through the host (open chests: ' + $chest.a.openChests + ' in both).')

Send 'test-break' 'b'
$barrel = Wait-Pair { param($a, $b) $a.brokenBarrels -gt $base.a.brokenBarrels -and $b.brokenBarrels -eq $a.brokenBarrels } 'Guest barrel break'
$settled = Wait-Pair { param($a, $b) $a.groundItems -eq $b.groundItems } 'Ground after chest and barrel loot'
Write-Output ('Guest broke a barrel through the host. Ground items match after the loot: ' + $settled.a.groundItems + '.')

[ordered]@{ passed = $true; base = $base; guestDrop = $guestDrop; hostTake = $hostTake; hostDrop = $hostDrop; guestTake = $guestTake; chest = $chest; barrel = $barrel; settled = $settled } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\shared-loot-verification.json')
Write-Output 'PASS: shared drops both ways, first-grab pickups without duplication, guest chests and barrels.'
