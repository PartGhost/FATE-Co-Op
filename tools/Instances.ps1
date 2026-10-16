$TestInstances = @('a', 'b', 'c')
function Get-InstanceSuffix([string]$Instance) { if ($Instance -eq 'a') { '' } else { '-' + $Instance } }
function Get-TestGamePath([string]$Instance) { Join-Path $workspace ('test-game' + (Get-InstanceSuffix $Instance)) }
function Get-RuntimePath([string]$Instance) { Join-Path $workspace ('artifacts\runtime' + (Get-InstanceSuffix $Instance)) }
function Get-SavePath([string]$Instance) { Join-Path $workspace ('test-saves\instance-' + $Instance) }
