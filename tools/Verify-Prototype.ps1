$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$save = Join-Path $env:USERPROFILE 'AppData\LocalLow\Tableflip\FATE Reawakened\FateMainSave.save'
$assembly = 'C:\Program Files (x86)\Steam\steamapps\common\FATE Reawakened\FateReawakened_Data\Managed\Assembly-CSharp.dll'
$originalSave = Get-Content -LiteralPath (Join-Path $workspace 'artifacts\original-save.json') -Raw | ConvertFrom-Json
$originalAssembly = Get-Content -LiteralPath (Join-Path $workspace 'artifacts\original-assembly.json') -Raw | ConvertFrom-Json
$saveUnchanged = (Get-FileHash -LiteralPath $save).Hash -eq $originalSave.Hash
$assemblyUnchanged = (Get-FileHash -LiteralPath $assembly).Hash -eq $originalAssembly.Hash
if (!$saveUnchanged -or !$assemblyUnchanged) { throw 'Original game/save protection check failed.' }
$samples = @(Get-Content -LiteralPath (Join-Path $workspace 'artifacts\runtime\telemetry.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.campaign -eq 'Original' -and $_.guestId -ne 0 })
function Get-Distance($a, $b) {
    [Math]::Sqrt([Math]::Pow($a.x - $b.x, 2) + [Math]::Pow($a.y - $b.y, 2) + [Math]::Pow($a.z - $b.z, 2))
}
$guestOnly = 0
$primaryOnly = 0
for ($i = 1; $i -lt $samples.Count; $i++) {
    $previous = $samples[$i - 1]
    $current = $samples[$i]
    if ($current.guestId -ne $previous.guestId -or $current.primaryId -ne $previous.primaryId -or $current.time - $previous.time -gt 2 -or $current.time -le $previous.time) { continue }
    $guestDistance = Get-Distance $current.guestPosition $previous.guestPosition
    $primaryDistance = Get-Distance $current.primaryPosition $previous.primaryPosition
    if ($guestDistance -gt 0.5 -and $primaryDistance -lt 0.02) { $guestOnly++ }
    if ($primaryDistance -gt 0.5 -and $guestDistance -lt 0.02) { $primaryOnly++ }
}
$result = [ordered]@{
    campaign = 'Original FATE / Grove'
    originalSaveUnchanged = $saveUnchanged
    originalAssemblyUnchanged = $assemblyUnchanged
    samples = $samples.Count
    guestMovesWhilePrimaryStill = $guestOnly
    primaryMovesWhileGuestStill = $primaryOnly
    walkingAnimationObserved = @($samples | Where-Object {$_.guestWalking}).Count -gt 0
    renderedMeshObserved = @($samples | Where-Object {$_.meshesSeenByCamera -gt 0}).Count -gt 0
    duplicateActorObserved = @($samples | Where-Object {$_.prototypeActors -gt 1}).Count -gt 0
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\runtime\verification.json')
$result | ConvertTo-Json
if ($samples.Count -eq 0 -or $guestOnly -eq 0 -or $primaryOnly -eq 0 -or !$result.walkingAnimationObserved -or !$result.renderedMeshObserved -or $result.duplicateActorObserved) {
    throw 'Movement verification is incomplete or failed; inspect the telemetry before calling the prototype verified.'
}
