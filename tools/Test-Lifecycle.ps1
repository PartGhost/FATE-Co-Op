$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$statusPath = Join-Path $workspace 'artifacts\runtime\status.json'
function Wait-State([scriptblock]$Condition, [int]$Timeout = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        Start-Sleep -Milliseconds 250
        try { $state = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json } catch { continue }
        if (& $Condition $state) { return $state }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Timed out waiting for game state: ' + ($state | ConvertTo-Json -Compress))
}
function Send([string]$Command) { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command }
$state = Wait-State { param($s) $s.saveLoaded -and $s.time -lt 300 }
if ($state.primaryId -eq 0) {
    if ($state.gameScene -ne 'MainMenu_Grove') { Send 'menu' }
    $state = Wait-State { param($s) $s.gameScene -eq 'MainMenu_Grove' -or ($s.primaryId -ne 0 -and !$s.transitioning) }
    if ($state.primaryId -eq 0) { Send 'continue' }
}
$state = Wait-State { param($s) $s.primaryId -ne 0 -and !$s.transitioning }
if ($state.scene -eq 'Scene_Dungeon') { Send 'town' }
$state = Wait-State { param($s) $s.scene -eq 'Scene_Town_Grove' -and $s.primaryId -ne 0 -and !$s.transitioning }
$cycles = @()
for ($i = 0; $i -lt 3; $i++) {
    Send 'despawn'
    $state = Wait-State { param($s) $s.guestId -eq 0 -and $s.prototypeActors -eq 0 }
    Send 'spawn'
    $state = Wait-State { param($s) $s.guestId -ne 0 -and $s.prototypeActors -eq 1 -and $s.guestOnNavMesh }
    $cycles += [pscustomobject]@{ guestId = $state.guestId; primaryId = $state.primaryId; actorCount = $state.prototypeActors }
}
Write-Output 'Three spawn/despawn cycles passed.'
Send 'test-invincible on'
$beforeEpoch = $state.worldEpoch
Send 'enter-dungeon'
$dungeon = Wait-State { param($s) $s.scene -eq 'Scene_Dungeon' -and $s.dungeonDepth -eq 1 -and !$s.transitioning -and $s.guestOnNavMesh -and $s.prototypeActors -eq 1 -and $s.worldEpoch -gt $beforeEpoch } 60
Send 'test-invincible on'
Write-Output 'Guest respawned on Grove dungeon floor 1.'
$beforeEpoch = $dungeon.worldEpoch
Send 'next-floor'
$floor = Wait-State { param($s) $s.scene -eq 'Scene_Dungeon' -and $s.dungeonDepth -eq 2 -and !$s.transitioning -and $s.guestOnNavMesh -and $s.prototypeActors -eq 1 -and $s.worldEpoch -gt $beforeEpoch } 60
Send 'test-invincible on'
Write-Output 'Guest respawned after rebuilding dungeon floor 2.'
$beforeEpoch = $floor.worldEpoch
Send 'town'
$town = Wait-State { param($s) $s.scene -eq 'Scene_Town_Grove' -and !$s.transitioning -and $s.guestOnNavMesh -and $s.prototypeActors -eq 1 -and $s.worldEpoch -gt $beforeEpoch } 60
Send 'test-invincible off'
$report = [ordered]@{ cycles = $cycles; dungeonFloor1 = $dungeon; dungeonFloor2 = $floor; returnedToGrove = $town; passed = $true }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\runtime\lifecycle-result.json')
Write-Output 'Town/dungeon/floor/town lifecycle checks passed; primary invincibility removed.'
