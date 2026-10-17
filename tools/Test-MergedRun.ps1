$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function Read-State([string]$Instance) {
    $runtime = if ($Instance -eq 'a') { 'artifacts\runtime' } else { 'artifacts\runtime-b' }
    Get-Content -LiteralPath (Join-Path $workspace ($runtime + '\status.json')) -Raw | ConvertFrom-Json
}
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Until([scriptblock]$Condition, [string]$What) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 400
        try { $a = Read-State 'a'; $b = Read-State 'b' } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, transitioning, dungeonDepth, localDepth, dungeonRealm, worldKey, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, transitioning, dungeonDepth, localDepth, dungeonRealm, worldKey, status | ConvertTo-Json -Compress))
}
function Settled($s) { !$s.transitioning -and $s.primaryOnNavMesh }
function Together([int]$Run) {
    Wait-Until { param($a, $b) (Settled $a) -and (Settled $b) -and $a.dungeonDepth -eq $Run -and $b.dungeonDepth -eq $Run -and $a.worldKey -ne '' -and $a.worldKey -eq $b.worldKey -and $a.layoutHash -eq $b.layoutHash } "Both on run floor $Run"
}
function Join([int]$Run) {
    Wait-Until { param($a, $b) (Settled $a) -and $a.dungeonDepth -eq $Run -and $a.worldKey -ne '' } "Host on run floor $Run" | Out-Null
    Send "test-run-floor $Run" 'b'
}

foreach ($instance in @('a','b')) { if ((Read-State $instance).gameScene -eq 'Dungeon') { Send 'town' $instance } }
$town = Wait-Until { param($a, $b) $a.gameScene -eq 'Town_Grove' -and $b.gameScene -eq 'Town_Grove' -and (Settled $a) -and (Settled $b) -and $a.networkConnected -and $b.networkConnected } 'Both in Grove'
Send 'test-resume'; Send 'test-resume' 'b'
$offsets = @{}
foreach ($section in $town.a.runSections -split ',') { $realm, $offset = $section -split ':'; $offsets[$realm] = [int]$offset }
$groveEnd = $offsets['Druantia']
Write-Output ('Run order: ' + $town.a.runSections + ' (Grove ends on floor ' + $groveEnd + ').')

Send "test-run-floor $groveEnd"
Join $groveEnd
$grove = Together $groveEnd
if ($grove.a.dungeonRealm -ne 'Grove' -or $grove.a.localDepth -ne $groveEnd) { throw "Run floor $groveEnd is not Grove floor $groveEnd." }

Send 'test-resume'; Send 'test-resume' 'b'
Send 'test-stairs down'
Join ($groveEnd + 1)
$druantia = Together ($groveEnd + 1)
foreach ($s in @($druantia.a, $druantia.b)) { if ($s.dungeonRealm -ne 'Druantia' -or $s.localDepth -ne 1) { throw ('The stairs below the last Grove floor went to ' + $s.dungeonRealm + ' ' + $s.localDepth) } }
if ($druantia.a.effectiveDepth -ne $grove.a.effectiveDepth + 1 -or $druantia.a.itemDepth -lt $groveEnd + 1) { throw ('Druantia 1 does not carry the depth: effective ' + $druantia.a.effectiveDepth + ', item depth ' + $druantia.a.itemDepth) }
Write-Output ('Stairs down from Grove ' + $groveEnd + ' lead both players to Druantia 1 (effective depth ' + $druantia.a.effectiveDepth + ', item depth ' + $druantia.a.itemDepth + ', monster levels ' + $druantia.a.monsterLevels + ').')

Send 'test-resume'; Send 'test-resume' 'b'
Send 'test-stairs up'
Join $groveEnd
$back = Together $groveEnd
if ($back.a.dungeonRealm -ne 'Grove' -or $back.a.layoutHash -ne $grove.a.layoutHash) { throw 'The stairs up from Druantia 1 did not return to the same last Grove floor.' }
Write-Output 'Stairs up from Druantia 1 lead back to the saved last Grove floor for both players.'

$realms = 'Typhon', 'TempleOfFate', 'ChamberOfTrials', 'CavernsLivingFire', 'TkalaCrypt', 'HauntedPalace'
if ($town.a.dlcs -ne 0 -and $town.b.dlcs -ne 0) { $realms += 'Prometheus' } else { Write-Output 'Skipping Prometheus: these test copies do not own the DLC.' }
foreach ($realm in $realms) {
    Send 'test-resume'; Send 'test-resume' 'b'
    Send ('test-run-floor ' + $offsets[$realm])
    Wait-Until { param($a, $b) (Settled $a) -and $a.dungeonDepth -eq $offsets[$realm] } "Last floor before $realm" | Out-Null
    Send 'test-resume'
    Send 'test-stairs down'
    $next = Wait-Until { param($a, $b) (Settled $a) -and $a.dungeonDepth -eq $offsets[$realm] + 1 } "First floor of $realm"
    if ($next.a.dungeonRealm -ne $realm -or $next.a.localDepth -ne 1) { throw ("The stairs into $realm went to " + $next.a.dungeonRealm + ' ' + $next.a.localDepth) }
    Write-Output ("The stairs down from floor " + $offsets[$realm] + " lead into $realm 1.")
}

if ($realms -contains 'Prometheus') {
    Send 'test-resume'
    $ownedDlcs = $town.b.dlcs
    Send 'test-dlcs 0' 'b'
    Start-Sleep -Seconds 2
    $closed = $offsets['Prometheus'] + 2
    Send "test-run-floor $closed"
    $palace = Wait-Until { param($a, $b) (Settled $a) -and $a.dungeonDepth -eq $closed } 'Run floor past the Haunted Palace with a friend who lacks Prometheus'
    Send "test-dlcs $ownedDlcs" 'b'
    if ($palace.a.dungeonRealm -ne 'HauntedPalace') { throw ('With a friend who lacks Prometheus, run floor ' + $closed + ' went to ' + $palace.a.dungeonRealm) }
    Write-Output ('With a friend who lacks the Prometheus DLC, run floor ' + $closed + ' stays in the Haunted Palace (floor ' + $palace.a.localDepth + ').')
}

Send 'test-resume'; Send 'test-resume' 'b'
$portalFloor = $offsets['Druantia'] + 2
Send "test-run-floor $portalFloor"
Join $portalFloor
Together $portalFloor | Out-Null
Send 'test-portal'
Start-Sleep -Seconds 1
Send 'test-use-portal own'
Wait-Until { param($a, $b) $a.gameScene -eq 'Town_Grove' -and (Settled $a) } 'Host back in Grove by portal' | Out-Null
Send 'test-resume'
Send 'test-dump P_Portal'
Start-Sleep -Seconds 2
$townPortals = (Get-Content -LiteralPath (Join-Path $workspace 'artifacts\runtime\dump.txt') | Select-String '^SCENE Scene_Town_Grove').Count
if ($townPortals -ne 1) { throw "Grove shows $townPortals of the host's own portals." }

$npcs = (Read-State 'a').townNpcs
foreach ($npc in 'Twigger', 'Cyrus', 'Malkondra') { if ($npcs -notmatch $npc) { throw "$npc is missing from Grove: $npcs" } }
Send 'test-npc Twigger'
$story = Wait-Until { param($a, $b) $a.prompt -ne '' } 'Twigger talking'
Send 'test-answer no'
if ($story.a.prompt -notmatch 'Druantia') { throw ('Twigger did not offer the Druantia story: ' + $story.a.prompt) }
Wait-Until { param($a, $b) $a.prompt -eq '' } 'Twigger done talking' | Out-Null
Send 'test-npc Malkondra'
$trial = Wait-Until { param($a, $b) $a.prompt -ne '' } 'Malkondra talking'
Send 'test-answer no'
if ($trial.a.prompt -notmatch 'Skeleton|Spider|Minotaur|Taurus|Centaur|Goliath|level') { throw ('Malkondra did not offer a trial: ' + $trial.a.prompt) }
Wait-Until { param($a, $b) $a.prompt -eq '' } 'Malkondra done talking' | Out-Null
Send 'test-npc XerxestheLibrarian'
$caverns = Wait-Until { param($a, $b) $a.prompt -ne '' } 'Xerxes talking'
Send 'test-answer no'
if ($caverns.a.prompt -notmatch 'Awadji') { throw ('Xerxes did not offer the Caverns story after the Caverns were reached: ' + $caverns.a.prompt) }
Write-Output 'Xerxes gives the Caverns of Living Fire story once the Caverns are reached.'
Write-Output ('Twigger, Cyrus and Malkondra stand in Grove once their realms are reached; Twigger offers the Druantia story and Malkondra says: ' + $trial.a.prompt)

Send 'test-use-portal own'
$return = Wait-Until { param($a, $b) $a.gameScene -eq 'Dungeon' -and (Settled $a) -and $a.dungeonDepth -eq $portalFloor } 'Host back through the portal'
if ($return.a.dungeonRealm -ne 'Druantia' -or $return.a.localDepth -ne 2) { throw 'The portal from Druantia 2 did not lead back to it.' }
Write-Output 'A town portal from Druantia 2 opens in Grove (one portal there) and leads back to Druantia 2.'

Send 'test-resume'; Send 'test-resume' 'b'
Write-Output 'PASS: the Grove, Undiscovered Realms, the Chamber of Trials, The Cursed King and Prometheus (for owners) form one run with carried depth, for one player and two.'
