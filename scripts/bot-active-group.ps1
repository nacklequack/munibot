[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$BotUrl,
    [guid]$GroupId,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
if (-not $env:MUNIBOT_OWNER_TOKEN) { throw 'Set MUNIBOT_OWNER_TOKEN through private operator configuration.' }
if ($BotUrl.Scheme -ne 'https' -and -not $BotUrl.IsLoopback) { throw 'Use HTTPS or a loopback endpoint.' }
if ($BotUrl.UserInfo -or $BotUrl.Query -or $BotUrl.Fragment) { throw 'Supply the bot base URL without credentials, query, or fragment.' }
$uri = $BotUrl.AbsoluteUri.TrimEnd('/') + '/api/bot/active-group'
$headers = @{ 'X-Munibot-Token' = $env:MUNIBOT_OWNER_TOKEN }
try {
    if ($PSBoundParameters.ContainsKey('GroupId')) {
        if ($GroupId -eq [guid]::Empty) { throw 'A nonzero group UUID is required.' }
        $body = @{ groupId = $GroupId.ToString() } | ConvertTo-Json
        $result = Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec $TimeoutSeconds
        if (-not $result.success -or $result.activeGroup.groupId -ne $GroupId.ToString() -or $result.activeGroup.pendingGroupId) {
            throw 'The response did not confirm the requested active group. Read the active group before retrying.'
        }
    } else {
        $result = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -TimeoutSec $TimeoutSeconds
    }
    $result | ConvertTo-Json -Depth 5
} finally {
    $headers.Clear()
}
