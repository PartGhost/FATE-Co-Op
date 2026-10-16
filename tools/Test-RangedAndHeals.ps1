$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State([string]$Instance = 'a') { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
function Send([string]$Command, [string]$Instance = 'a') { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }

& (Join-Path $PSScriptRoot 'Enter-HostDungeon.ps1') | Out-Null
Send 'test-bow'
Start-Sleep -Seconds 1
$before = State
if (!$before.rangedEquipped) { throw 'The bow did not equip.' }
for ($i = 0; $i -lt 30 -and (State).rangedRolls -lt $before.rangedRolls + 20; $i++) {
    Send 'test-hit'
    Start-Sleep -Milliseconds 300
}
$after = State
$rolls = $after.rangedRolls - $before.rangedRolls
if ($rolls -lt 20) { throw ('Only ' + $rolls + ' ranged rolls happened.') }
if ($after.rangedMisses -ne $before.rangedMisses) { throw ('Ranged shots missed: ' + ($after.rangedMisses - $before.rangedMisses)) }
Write-Output ('Ranged: ' + $rolls + ' shots, no misses, ' + ($after.grazes - $before.grazes) + ' grazes.')
$guest = State 'b'
Send 'test-shift-heal'
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while ((State 'b').effectsTaken -le $guest.effectsTaken -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 400 }
if ((State 'b').effectsTaken -le $guest.effectsTaken) { throw 'The Shift heal never reached the partner.' }
Write-Output 'Shift + heal landed on the partner.'
Write-Output 'PASS: ranged shots in range always land (some as grazes), and Shift casts heals on a friend.'
