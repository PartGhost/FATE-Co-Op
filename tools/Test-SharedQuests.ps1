$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function State([string]$Instance) {
    $path = if ($Instance -eq 'a') { 'artifacts\runtime\status.json' } else { 'artifacts\runtime-b\status.json' }
    Get-Content -LiteralPath (Join-Path $workspace $path) -Raw | ConvertFrom-Json
}
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
function Wait-Pair([scriptblock]$Condition, [string]$What, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 400
        try { $a = State a; $b = State b } catch { continue }
        if (& $Condition $a $b) { return [pscustomobject]@{ a = $a; b = $b } }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: A=' + ($a | Select-Object gameScene, questRewardsSent, primaryExperience, status | ConvertTo-Json -Compress) + ' B=' + ($b | Select-Object gameScene, partyQuests, questRewardsTaken, primaryExperience, status | ConvertTo-Json -Compress))
}

if ((State a).gameScene -eq 'Dungeon') { Send 'town' }
if ((State b).gameScene -eq 'Dungeon') { Send 'town' 'b' }
$town = Wait-Pair { param($a, $b) $a.gameScene -eq 'Town_Grove' -and $b.gameScene -eq 'Town_Grove' -and $a.networkConnected -and $b.networkConnected } 'Both players in Grove' 90
Send 'test-accept-quest'
$listed = Wait-Pair { param($a, $b) $a.partyQuests -ne '' -and $b.partyQuests -eq $a.partyQuests } 'Shared quest list on the guest'
Write-Output ('Guest sees the host''s quests: ' + ($listed.b.partyQuests -replace "`n", ' | '))

Send 'test-finish-quest'
$rewarded = Wait-Pair { param($a, $b) $a.questRewardsSent -gt $town.a.questRewardsSent -and $b.questRewardsTaken -gt $town.b.questRewardsTaken -and $b.primaryExperience -gt $town.b.primaryExperience -and $a.primaryExperience -gt $town.a.primaryExperience } 'Shared quest reward'
Write-Output ('Host turned the quest in; both got the reward (XP A ' + $town.a.primaryExperience + ' -> ' + $rewarded.a.primaryExperience + ', B ' + $town.b.primaryExperience + ' -> ' + $rewarded.b.primaryExperience + ').')

Send 'test-accept-quest'
$second = Wait-Pair { param($a, $b) $a.partyQuests -ne '' -and $b.partyQuests -eq $a.partyQuests } 'Second shared quest'
Send 'test-finish-quest' 'b'
$guestClaim = Wait-Pair { param($a, $b) $a.primaryExperience -gt $second.a.primaryExperience -and $b.primaryExperience -gt $second.b.primaryExperience -and $b.partyQuests -eq $a.partyQuests } 'Guest turning in a shared quest'
Start-Sleep -Seconds 3
if ((State b).questRewardsTaken -ne $second.b.questRewardsTaken) { throw 'The guest got the reward twice.' }
Write-Output 'Guest turned a shared quest in: the host''s copy was claimed too, both got the reward once.'

Send 'test-accept-quest 2' 'b'
$guestAccept = Wait-Pair { param($a, $b) $a.giverQuests -match ':Accepted' -and $a.partyQuests -ne '' -and $b.partyQuests -eq $a.partyQuests } 'Guest accepting a quest'
Send 'test-finish-quest only' 'b'
$guestDone = Wait-Pair { param($a, $b) $a.giverQuests -match ':Completed' } 'Guest completing a quest'
Send 'test-finish-quest' 'b'
Wait-Pair { param($a, $b) $a.partyQuests -eq '' -and $b.partyQuests -eq '' } 'Guest turning in its own quest' | Out-Null
Write-Output ('Guest accepted, completed and turned in a quest; the host saw each step (' + $guestAccept.a.giverQuests + ', then ' + $guestDone.a.giverQuests + ').')

$groveEnd = [int](((State a).runSections -split ',')[1] -split ':')[1]
Send 'test-quest-depth 60'
Start-Sleep -Seconds 2
if ((State a).status -notmatch 'new ([\d,]+), loaded (\d+)') { throw ('No quest depth report: ' + (State a).status) }
$deepest = ($Matches[1] -split ',' | ForEach-Object { [int]$_ } | Measure-Object -Maximum).Maximum
if ($deepest -gt $groveEnd -or [int]$Matches[2] -gt $groveEnd) { throw ('Grove side quests reach past the Grove: ' + (State a).status) }
Write-Output ('Grove side quests stay on the Grove''s ' + $groveEnd + ' floors: ' + (State a).status)

[ordered]@{ passed = $true; town = $town; listed = $listed; rewarded = $rewarded; guestClaim = $guestClaim; guestAccept = $guestAccept; guestDone = $guestDone } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\shared-quests-verification.json')
Write-Output 'PASS: one shared quest list, rewards for everyone whoever turns the quest in.'
