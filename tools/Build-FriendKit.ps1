$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build-Prototype.ps1')
$stage = Join-Path $workspace 'artifacts\friend-kit'
$zip = Join-Path $workspace 'artifacts\FateCoop-Kit.zip'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$kit = Join-Path $stage 'kit'
$dotnet = Join-Path $PSScriptRoot 'dotnet'
$patcher = Join-Path $workspace 'src\SandboxPatcher\bin\Release\net8.0'
New-Item -ItemType Directory -Force -Path (Join-Path $kit 'bepinex'), (Join-Path $kit 'dotnet\shared'), (Join-Path $kit 'patcher') | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'kit\*') -Destination $stage
Copy-Item -Path (Join-Path $PSScriptRoot 'bepinex\*') -Destination (Join-Path $kit 'bepinex') -Recurse
Copy-Item -LiteralPath (Join-Path $dotnet 'dotnet.exe'), (Join-Path $dotnet 'LICENSE.txt'), (Join-Path $dotnet 'ThirdPartyNotices.txt') -Destination (Join-Path $kit 'dotnet')
Copy-Item -LiteralPath (Join-Path $dotnet 'host') -Destination (Join-Path $kit 'dotnet') -Recurse
Copy-Item -LiteralPath (Join-Path $dotnet 'shared\Microsoft.NETCore.App') -Destination (Join-Path $kit 'dotnet\shared') -Recurse
foreach ($file in @('SandboxPatcher.dll', 'SandboxPatcher.deps.json', 'SandboxPatcher.runtimeconfig.json', 'Mono.Cecil.dll')) {
    Copy-Item -LiteralPath (Join-Path $patcher $file) -Destination (Join-Path $kit 'patcher')
}
Copy-Item -LiteralPath (Join-Path $workspace 'src\FateCoop.Prototype\bin\Release\netstandard2.1\FateCoop.Prototype.dll') -Destination $kit
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Output ('Friend kit: ' + $zip + ' (' + [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB) + ' MB)')
