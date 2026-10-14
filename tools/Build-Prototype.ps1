$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$dotnet = Join-Path $PSScriptRoot 'dotnet\dotnet.exe'
Push-Location $workspace
try {
    if (Get-Process -Name FateReawakened -ErrorAction SilentlyContinue) { throw 'Close FATE before deploying a rebuilt prototype.' }
    foreach ($game in @('test-game','test-game-b')) {
        $lockPath = Join-Path $workspace ($game + '\fate-coop-running.lock')
        if (Test-Path -LiteralPath $lockPath) {
            try { $handle = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None'); $handle.Dispose() }
            catch { throw ('Close sandbox ' + $game + ' before rebuilding; its runtime lock is held.') }
        }
    }
    & $dotnet restore src\SandboxPatcher\SandboxPatcher.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox patcher restore failed.' }
    & $dotnet build src\SandboxPatcher\SandboxPatcher.csproj -c Release --nologo --no-restore -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox patcher build failed.' }
    & $dotnet src\SandboxPatcher\bin\Release\net8.0\SandboxPatcher.dll $workspace (Join-Path $workspace 'test-saves\instance-a')
    if ($LASTEXITCODE -ne 0) { throw 'Sandbox patching failed.' }
    & $dotnet restore src\FateCoop.Prototype\FateCoop.Prototype.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Prototype restore failed.' }
    & $dotnet build src\FateCoop.Prototype\FateCoop.Prototype.csproj -c Release --nologo --no-restore -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Prototype build failed.' }
    if (!(Test-Path -LiteralPath 'test-game\BepInEx\core\BepInEx.Core.dll')) {
        Copy-Item -Path 'tools\bepinex\*' -Destination 'test-game' -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path 'test-game\BepInEx\plugins' | Out-Null
    Copy-Item -LiteralPath 'src\FateCoop.Prototype\bin\Release\netstandard2.1\FateCoop.Prototype.dll' -Destination 'test-game\BepInEx\plugins'
    if (Test-Path -LiteralPath 'test-game-b') {
        & $dotnet src\SandboxPatcher\bin\Release\net8.0\SandboxPatcher.dll $workspace (Join-Path $workspace 'test-saves\instance-b') b
        if ($LASTEXITCODE -ne 0) { throw 'Instance B sandbox patching failed.' }
        Copy-Item -LiteralPath 'src\FateCoop.Prototype\bin\Release\netstandard2.1\FateCoop.Prototype.dll' -Destination 'test-game-b\BepInEx\plugins'
    }
    & $dotnet restore src\FateCoop.Client\FateCoop.Client.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Client restore failed.' }
    & $dotnet build src\FateCoop.Client\FateCoop.Client.csproj -c Release --nologo --no-restore -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
    & $dotnet src\FateCoop.Client\bin\Release\net8.0\FateCoop.Client.dll --protocol-test
    if ($LASTEXITCODE -ne 0) { throw 'Protocol verification failed.' }
} finally { Pop-Location }
