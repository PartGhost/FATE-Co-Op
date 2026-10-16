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
$listed = Wait-Pair { param($a, $b) $b.partyQuests -ne '' } 'Party quest list on the guest'
Write-Output ('Guest sees the host''s quests: ' + ($listed.b.partyQuests -replace "`n", ' | '))

Send 'test-finish-quest'
$rewarded = Wait-Pair { param($a, $b) $a.questRewardsSent -gt $town.a.questRewardsSent -and $b.questRewardsTaken -gt $town.b.questRewardsTaken -and $b.primaryExperience -gt $town.b.primaryExperience -and $a.primaryExperience -gt $town.a.primaryExperience } 'Shared quest reward'
Write-Output ('Host turned the quest in; both got the reward (XP A ' + $town.a.primaryExperience + ' -> ' + $rewarded.a.primaryExperience + ', B ' + $town.b.primaryExperience + ' -> ' + $rewarded.b.primaryExperience + ').')

[ordered]@{ passed = $true; town = $town; listed = $listed; rewarded = $rewarded } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\shared-quests-verification.json')
Write-Output 'PASS: the guest sees the party quests and gets its own reward when the host turns one in.'
