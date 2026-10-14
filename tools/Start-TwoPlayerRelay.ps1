param([ValidateRange(0,86400)][int]$Seconds = 0, [switch]$Background)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $PSScriptRoot 'dotnet\dotnet.exe'
$client = Join-Path $workspace 'src\FateCoop.Client\bin\Release\net8.0\FateCoop.Client.dll'
if ($Background) {
    $arguments = '"' + $client + '" --workspace "' + $workspace + '" --pair --seconds ' + $Seconds
    $process = Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $workspace -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $workspace 'artifacts\relay-output.log') -RedirectStandardError (Join-Path $workspace 'artifacts\relay-error.log')
    $process.Id | Set-Content (Join-Path $workspace 'artifacts\relay-process-id.txt')
    Write-Output ('Started background movement relay ' + $process.Id + '. Stop with tools\Stop-TwoPlayerRelay.ps1.')
    return
}
& $dotnet $client --workspace $workspace --pair --seconds $Seconds
if ($LASTEXITCODE -ne 0) { throw 'Two-instance relay failed; inspect its output.' }
