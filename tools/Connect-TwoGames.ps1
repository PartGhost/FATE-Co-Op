$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function State([string]$Instance) {
    $runtime = if ($Instance -eq 'a') { 'artifacts\runtime' } else { 'artifacts\runtime-b' }
    Get-Content -LiteralPath (Join-Path $workspace ($runtime + '\status.json')) -Raw | ConvertFrom-Json
}
function Wait-State([string]$Instance, [scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        try { $s = State $Instance } catch { continue }
        if (& $Condition $s) { return $s }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Instance ' + $Instance + ' timed out: ' + ($s | ConvertTo-Json -Compress))
}
function Send([string]$Command, [string]$Instance) { & (Join-Path $PSScriptRoot 'Send-PrototypeCommand.ps1') $Command -Instance $Instance }
foreach ($instance in @('a','b')) {
    Send 'leave' $instance
    Wait-State $instance { param($s) $s.sessionRole -eq '' } | Out-Null
}
Send 'host-local' 'a'
$a = Wait-State 'a' { param($s) $s.sessionRole -eq 'host' -and $s.localPort -gt 0 }
Send ('join-local ' + $a.localPort) 'b'
Wait-State 'a' { param($s) $s.networkConnected } | Out-Null
Wait-State 'b' { param($s) $s.networkConnected -and $s.sessionRole -eq 'guest' } | Out-Null
Write-Output ('A hosts on localhost port ' + $a.localPort + '; B joined. A selects floors; B follows. Send "leave" to either game to disconnect.')
