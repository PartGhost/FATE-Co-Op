function Get-ModNames {
    $src = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
    Get-ChildItem -LiteralPath $src -Directory | Where-Object {
        $project = Join-Path $_.FullName ($_.Name + '.csproj')
        (Test-Path -LiteralPath $project) -and (Select-String -LiteralPath $project -Pattern 'FateMod.props' -SimpleMatch -Quiet)
    } | ForEach-Object Name
}

function Get-ModDll([string]$Name) {
    Join-Path (Split-Path -Parent $PSScriptRoot) ('src\' + $Name + '\bin\Release\netstandard2.1\' + $Name + '.dll')
}
