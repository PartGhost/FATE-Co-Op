param([ValidateSet('a','b','c')][string]$Instance = 'a')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
$runtime = Get-RuntimePath $Instance
function Wait-State([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 250
        try { $s = Get-Content -LiteralPath (Join-Path $runtime 'status.json') -Raw | ConvertFrom-Json } catch { continue }
        if (& $Condition $s) { return $s }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Instance ' + $Instance + ' timed out: ' + ($s | ConvertTo-Json -Compress))
}
function Send([string]$Command) { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
$s = Wait-State { param($state) $state.saveLoaded }
if ($s.primaryId -eq 0) {
    if ($s.gameScene -ne 'MainMenu_Grove') { Send 'menu' }
    $s = Wait-State { param($state) $state.gameScene -eq 'MainMenu_Grove' }
    Send 'continue'
}
$s = Wait-State { param($state) $state.primaryId -ne 0 -and !$state.transitioning }
if ($s.gameScene -eq 'Dungeon') { Send 'town' }
$s = Wait-State { param($state) $state.gameScene -eq 'Town_Grove' -and !$state.transitioning -and $state.primaryOnNavMesh }
Send 'test-resume'
Write-Output ('Instance ' + $Instance + ' ready in Grove; primary ' + $s.primaryId)
