param(
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\FATE Reawakened'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$output = Join-Path $workspace 'research'
$tool = Join-Path $PSScriptRoot 'ilspycmd\tools\net8.0\any\ilspycmd.dll'
$assembly = Join-Path $GameRoot 'FateReawakened_Data\Managed\Assembly-CSharp.dll'
if (!(Test-Path -LiteralPath $tool)) { throw 'Local ILSpyCmd tool is missing.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
Get-FileHash -LiteralPath $assembly | ConvertTo-Json | Set-Content (Join-Path $output 'assembly-hash.json')
& dotnet $tool -l c $assembly | Set-Content (Join-Path $output 'types.txt')
if ($LASTEXITCODE -ne 0) { throw 'Type inventory failed.' }
$types = @(
    'Tableflip.Player', 'Tableflip.Character', 'Tableflip.PlayerManager',
    'Tableflip.PlayerSpawn', 'Tableflip.PartyMember', 'Tableflip.Monster',
    'Tableflip.LevelGenerator', 'Tableflip.Town', 'Tableflip.FateDB',
    'Tableflip.SaveData', 'Tableflip.FateSaveData', 'Tableflip.PlayerSaveData',
    'Tableflip.PartyMemberSaveData', 'Tableflip.DungeonSaveData',
    'Tivola.Framework.SaveManager', 'Tivola.Framework.SaveManagerStandalone',
    'Cleverous.Prodigy.ProdigyPlayer', 'Cleverous.Prodigy.ProdigyAgentLocomotion',
    'Cleverous.Prodigy.Input.InputBroker'
)
foreach ($type in $types) {
    & dotnet $tool -t $type $assembly | Set-Content (Join-Path $output ($type + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "Decompilation failed: $type" }
}
