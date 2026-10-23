$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Instances.ps1')
. (Join-Path $PSScriptRoot 'Mods.ps1')
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$dotnet = Join-Path $PSScriptRoot 'dotnet\dotnet.exe'
$instances = $TestInstances | Where-Object { Test-Path -LiteralPath (Get-TestGamePath $_) }
$mods = Get-ModNames
Push-Location $workspace
try {
    if (Get-Process -Name FateReawakened -ErrorAction SilentlyContinue) { throw 'Close FATE before deploying a rebuilt prototype.' }
    foreach ($instance in $instances) {
        $lockPath = Join-Path (Get-TestGamePath $instance) 'fate-coop-running.lock'
        if (Test-Path -LiteralPath $lockPath) {
            try { $handle = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None'); $handle.Dispose() }
            catch { throw ('Close test game ' + $instance + ' before rebuilding; its runtime lock is held.') }
        }
    }
    & $dotnet restore src\SandboxPatcher\SandboxPatcher.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox patcher restore failed.' }
    & $dotnet build src\SandboxPatcher\SandboxPatcher.csproj -c Release --nologo --no-restore -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox patcher build failed.' }
    foreach ($mod in $mods) {
        & $dotnet restore ('src\' + $mod + '\' + $mod + '.csproj') --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw ($mod + ' restore failed.') }
        & $dotnet build ('src\' + $mod + '\' + $mod + '.csproj') -c Release --nologo --no-restore -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw ($mod + ' build failed.') }
    }
    foreach ($instance in $instances) {
        $game = Get-TestGamePath $instance
        & $dotnet src\SandboxPatcher\bin\Release\net8.0\SandboxPatcher.dll $workspace (Get-SavePath $instance) $instance
        if ($LASTEXITCODE -ne 0) { throw ('Sandbox patching failed for test game ' + $instance + '.') }
        if (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx\core\BepInEx.Core.dll'))) { Copy-Item -Path 'tools\bepinex\*' -Destination $game -Recurse -Force }
        $plugins = Join-Path $game 'BepInEx\plugins\FateCoop'
        if (Test-Path -LiteralPath $plugins) { Remove-Item -LiteralPath $plugins -Recurse -Force }
        Remove-Item -LiteralPath (Join-Path $game 'BepInEx\plugins\FateCoop.Prototype.dll') -ErrorAction SilentlyContinue
        foreach ($mod in $mods) {
            $folder = New-Item -ItemType Directory -Force -Path (Join-Path $plugins $mod)
            Copy-Item -LiteralPath (Get-ModDll $mod) -Destination $folder.FullName
        }
        New-Item -ItemType Directory -Force -Path (Join-Path $game 'BepInEx\config') | Out-Null
        [IO.File]::WriteAllText((Join-Path $game 'BepInEx\config\local.fatecoop.core.cfg'), "[Development]`nTestTools = true`n")
    }
} finally { Pop-Location }
