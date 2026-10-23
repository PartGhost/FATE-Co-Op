$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Mods.ps1')
& (Join-Path $PSScriptRoot 'Build-Prototype.ps1')
$dotnet = Join-Path $PSScriptRoot 'dotnet\dotnet.exe'
$installer = Join-Path $workspace 'src\FateCoop.Installer\FateCoop.Installer.csproj'
& $dotnet restore $installer --configfile (Join-Path $workspace 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Installer restore failed.' }
& $dotnet build $installer -c Release --nologo --no-restore -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$stage = Join-Path $workspace 'artifacts\mod'
$zip = Join-Path $workspace 'artifacts\FateCoop-Mod.zip'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$plugin = Join-Path $stage 'BepInEx\plugins\FateCoop'
New-Item -ItemType Directory -Force -Path $plugin | Out-Null
$bepinex = Join-Path $PSScriptRoot 'bepinex'
Copy-Item -LiteralPath (Join-Path $bepinex 'winhttp.dll'), (Join-Path $bepinex 'doorstop_config.ini'), (Join-Path $bepinex '.doorstop_version') -Destination $stage
Copy-Item -LiteralPath (Join-Path $bepinex 'BepInEx\core') -Destination (Join-Path $stage 'BepInEx') -Recurse
Copy-Item -LiteralPath (Join-Path $workspace 'LICENSE'), (Join-Path $workspace 'THIRD-PARTY-NOTICES.txt') -Destination $plugin
foreach ($mod in Get-ModNames) {
    $folder = New-Item -ItemType Directory -Force -Path (Join-Path $plugin $mod)
    Copy-Item -LiteralPath (Get-ModDll $mod) -Destination $folder.FullName
}
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File -Force) {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $file.FullName.Substring($stage.Length + 1).Replace('\', '/')) | Out-Null
}
$archive.Dispose()
Copy-Item -LiteralPath (Join-Path $workspace 'src\FateCoop.Installer\bin\Release\net48\FateCoop-Installer.exe') -Destination (Join-Path $workspace 'artifacts') -Force
Write-Output ('Release files: ' + $zip + ' (' + [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1) + ' MB) and artifacts\FateCoop-Installer.exe')
