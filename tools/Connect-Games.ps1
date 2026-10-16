param([string[]]$Instances = @('a', 'b'))
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
function State([string]$Instance) { Get-Content -LiteralPath (Join-Path (Get-RuntimePath $Instance) 'status.json') -Raw | ConvertFrom-Json }
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
foreach ($instance in $Instances) {
    Send 'leave' $instance
    Wait-State $instance { param($s) $s.sessionRole -eq '' } | Out-Null
}
$hostInstance = $Instances[0]
Send 'host-local' $hostInstance
$hosted = Wait-State $hostInstance { param($s) $s.sessionRole -eq 'host' -and $s.localPort -gt 0 }
foreach ($guest in $Instances | Select-Object -Skip 1) {
    Send ('join-local ' + $hosted.localPort) $guest
    Wait-State $guest { param($s) $s.networkConnected -and $s.sessionRole -eq 'guest' -and $s.myId -gt 0 } | Out-Null
}
$expected = $Instances.Count - 1
foreach ($instance in $Instances) { Wait-State $instance { param($s) $s.partnerCount -eq $expected } | Out-Null }
Write-Output ($hostInstance.ToUpper() + ' hosts on localhost port ' + $hosted.localPort + '; ' + (($Instances | Select-Object -Skip 1) -join ', ').ToUpper() + ' joined.')
