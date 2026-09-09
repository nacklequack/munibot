[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$BotUrl,
    [Parameter(Mandatory)][guid]$ObjectUuid,
    [Parameter(Mandatory)][string]$Region,
    [Parameter(Mandatory)][float]$X,
    [Parameter(Mandatory)][float]$Y,
    [Parameter(Mandatory)][float]$Z
)

$ErrorActionPreference = 'Stop'
if ($ObjectUuid -eq [guid]::Empty) { throw 'An exact disposable object UUID is required.' }
if (-not $env:MUNIBOT_TASK_TOKEN) { throw 'Set MUNIBOT_TASK_TOKEN through private test configuration.' }
if ($BotUrl.Scheme -ne 'https' -and -not $BotUrl.IsLoopback) { throw 'Use HTTPS or a loopback test endpoint.' }
$headers = @{ 'X-Munibot-Token' = $env:MUNIBOT_TASK_TOKEN }
$position = @{ x=$X; y=$Y; z=$Z }
$scriptName = 'munibase-replenishment-probe.lsl'
$cardName = 'munibase-replenishment-probe.notecard'
$base = $BotUrl.AbsoluteUri.TrimEnd('/') + '/api/objects/' + $ObjectUuid.ToString() + '/inventory/'

function Inspect-Probes {
    $body = @{ region=$Region; position=$position; names=@($scriptName,$cardName) } | ConvertTo-Json -Depth 5
    $result = Invoke-RestMethod -Method Post -Uri ($base+'inspect') -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 180
    if ($result.objectUuid -ne $ObjectUuid.ToString() -or $null -eq $result.items) { throw 'Inspection did not prove the target inventory.' }
    return $result
}

function Write-Probe([string]$name, [string]$kind, [string]$source) {
    $normalized = $source.Replace("`r`n","`n").Replace("`r","`n")
    if (-not $normalized.EndsWith("`n")) { $normalized += "`n" }
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
    $body = @{ region=$Region; position=$position; contentType=$kind; sourceDataBase64=[Convert]::ToBase64String($bytes);
        expectedSha256=$hash; requestId=[guid]::NewGuid().ToString() } | ConvertTo-Json -Depth 5
    $result = Invoke-RestMethod -Method Put -Uri ($base+'items/'+[uri]::EscapeDataString($name)) -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 180
    if ($result.success -and ($result.item.sourceSha256 -ne $hash -or ($kind -eq 'script' -and $result.item.running -ne $false))) {
        throw 'Success response did not verify source hash and stopped state.'
    }
    return $result
}

if (@((Inspect-Probes).items).Count -ne 0) { throw 'Disposable probe names already exist. Inspect them manually before using another target with only an unrelated seed notecard.' }
$created = Write-Probe $scriptName 'script' 'string SCRIPT_VERSION = "1"; default { state_entry() { } }'
if (-not $created.success) { throw 'New script did not compile and verify.' }
$updated = Write-Probe $scriptName 'script' 'string SCRIPT_VERSION = "2"; default { state_entry() { } }'
if (-not $updated.success -or $updated.item.itemId -ne $created.item.itemId) { throw 'Script update did not preserve the task item UUID.' }
$failed = Write-Probe $scriptName 'script' 'default { state_entry() { this cannot compile } }'
if ($failed.compiled -ne $false -or @($failed.compilationMessages).Count -eq 0) { throw 'Compiler failure was not reported with diagnostics.' }
$repaired = Write-Probe $scriptName 'script' 'string SCRIPT_VERSION = "3"; default { state_entry() { } }'
if (-not $repaired.success) { throw 'Script repair did not verify.' }
$card = Write-Probe $cardName 'notecard' "BundleId=disposable-test`nScript=$scriptName|3`n"
if (-not $card.success) { throw 'New notecard did not verify.' }
$cardUpdate = Write-Probe $cardName 'notecard' "BundleId=disposable-update`nScript=$scriptName|3`n"
if (-not $cardUpdate.success -or $card.item.itemId -ne $cardUpdate.item.itemId) { throw 'Notecard replacement did not preserve its task item UUID.' }
if (@((Inspect-Probes).items).Count -ne 2) { throw 'Final inventory contains a missing or duplicated probe.' }
Write-Output 'PASS: created and updated stopped script; compile failure and repair; notecard creation and replacement; readback verified.'
Write-Output 'Permission arrangements, interrupted connectivity, real distributor manifest reload, and operator rollout require the remaining in-world acceptance steps.'
