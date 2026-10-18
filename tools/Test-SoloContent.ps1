$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State { Get-Content -LiteralPath (Join-Path (Get-RuntimePath 'a') 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command) { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance a }
function Wait-Until([scriptblock]$Condition, [string]$What, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 300
        try { $s = State } catch { continue }
        if (& $Condition $s) { return $s }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ($What + ' timed out: ' + ($s | Select-Object gameScene, skillPoints, gold, localPet, buffs, primaryExperience, bagCheck, status | ConvertTo-Json -Compress))
}

& (Join-Path $PSScriptRoot 'Open-TestGrove.ps1') -Instance a | Out-Null
$start = State
if ($start.bagCheck -ne 'overlaps=0 mismatched=0') { throw ('The bag grid is out of step with its items: ' + $start.bagCheck) }

Send 'test-spend SkillSword 2'
$spent = Wait-Until { param($s) $s.skillPoints -eq $start.skillPoints - 2 } 'Spending two skill points'
Send 'test-npc SakinathePriestess yes'
$reset = Wait-Until { param($s) $s.skillPoints -eq $start.skillPoints -and $s.gold -eq $spent.gold - 200 } 'Skill reset at Sakina'
Write-Output ('Sakina gave back 2 skill points for ' + ($spent.gold - $reset.gold) + ' gold.')

Send 'test-kill-pet'
Start-Sleep 1
$pet = State
if ($pet.localPet -notmatch 'dead=False') { throw ('The pet died: ' + $pet.localPet) }
Write-Output ('The pet shrugged off a death: ' + $pet.localPet)

Send 'test-buff'
$buffed = Wait-Until { param($s) @($s.buffs -split ',' | Where-Object { $_ -match ':(\d+)$' -and [int]$Matches[1] -ge 140 }).Count -gt 0 } 'A long spell buff'
Write-Output ('Spell buffs last at least two and a half minutes: ' + $buffed.buffs)

Send 'test-bow'
Send 'test-socket Item_Sagestone'
Start-Sleep 1
$before = State
Send 'test-levelup'
$after = Wait-Until { param($s) $s.primaryExperience -gt $before.primaryExperience } 'Experience award'
if ($after.primaryExperience - $before.primaryExperience -ne 52000) { throw ('A Sagestone should turn 50000 experience into 52000, got ' + ($after.primaryExperience - $before.primaryExperience)) }
Write-Output 'A socketed Sagestone gives 4% more experience (50000 -> 52000).'

Send 'test-back Item_LightQuiver'
Start-Sleep 1
if ((State).capeBend -ge 0) { throw 'A quiver got cape physics.' }
Send 'test-back Item_BloodmoonCloak'
Start-Sleep 1
Send 'test-walk 1 0'
$swung = Wait-Until { param($s) $s.capeBend -gt 10 } 'A cape bending while walking'
Write-Output ('A cape bends while walking (' + [Math]::Round($swung.capeBend) + ' degrees) and a quiver stays rigid.')
