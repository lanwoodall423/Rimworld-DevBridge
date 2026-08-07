# Durable review queue behavioral coverage.
[CmdletBinding()]
param(
    [string] $BridgeRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$client = Join-Path $BridgeRoot 'DevTools/devbridge.ps1'
$userRoot = Join-Path ([IO.Path]::GetTempPath()) ('RimWorldDevBridgeReview-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $userRoot -Force | Out-Null

function Invoke-Client([string[]] $arguments) {
    $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $client @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $jsonLine = @($output | ForEach-Object { [string]$_ } | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
    if ($jsonLine.Count -eq 0) { throw "review client returned no JSON: $($output -join ' ')" }
    return [pscustomobject]@{ ExitCode = $exitCode; Json = ($jsonLine[0] | ConvertFrom-Json) }
}

function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}

try {
    $common = @(
        'review', 'request', '--user-root', $userRoot, '--task-id', 'review-queue-task',
        '--category', 'human_review', '--question', 'Choose a bounded review option',
        '--option-1', 'Continue', '--option-2', 'Pause', '--recommended', 'Continue',
        '--resume-operation', 'review resume --request-id review-queue-request',
        '--completed-work', 'build and tests complete', '--verification-evidence', 'portable checks pass',
        '--dependent-work', 'human selection only', '--independent-work', 'none',
        '--screenshot-ref', 'artifacts/review.png', '--artifact-ref', 'artifacts/review.json',
        '--branch', 'review-test', '--commit', 'test', '--dirty-state', 'clean',
        '--dedup-key', 'review-queue-dedup', '--response-timeout-ms', '1000'
    )
    $first = Invoke-Client $common
    Assert-True ($first.ExitCode -eq 0 -and $first.Json.request.state -eq 'WAITING_FOR_HUMAN') 'review request did not persist'
    Assert-True ($first.Json.request.authorization.authorizesMutation -eq $false) 'review granted mutation authority'
    Assert-True ($first.Json.request.authorization.grantsWriteLease -eq $false) 'review granted a write lease'
    Assert-True ($first.Json.request.screenshotReferences -contains 'artifacts/review.png' -and
        $first.Json.request.artifactReferences -contains 'artifacts/review.json') 'review evidence references were not persisted'
    $requestId = [string]$first.Json.request.requestId

    $duplicate = Invoke-Client $common
    Assert-True ($duplicate.Json.request.requestId -eq $requestId -and $duplicate.Json.request.deduplicated -eq $true) 'review request was not deduplicated'

    $listed = Invoke-Client @('review', 'list', '--user-root', $userRoot)
    Assert-True ($listed.Json.count -ge 1) 'review list was empty'

    $checkpoint = Invoke-Client @('review', 'checkpoint', '--user-root', $userRoot, '--request-id', $requestId, '--reason', 'autonomous work complete')
    Assert-True ($checkpoint.Json.request.state -eq 'READY_AWAITING_HUMAN' -and $checkpoint.Json.resourcesReleased -eq $true) 'checkpoint did not release resources'

    $resolved = Invoke-Client @('review', 'resolve', '--user-root', $userRoot, '--request-id', $requestId, '--selected-option', 'Continue', '--answer', 'Continue autonomously')
    Assert-True ($resolved.Json.request.state -eq 'RESOLVED') 'review resolve failed'
    $resumed = Invoke-Client @('review', 'resume', '--user-root', $userRoot, '--request-id', $requestId)
    Assert-True ($resumed.Json.canResume -eq $true -and $resumed.Json.authorization.grantsWriteLease -eq $false) 'review resume safety or state failed'

    $waitArgs = @(
        'review', 'request', '--user-root', $userRoot, '--request-id', 'review-wait-request', '--task-id', 'review-wait-task',
        '--category', 'human_review', '--question', 'Wait for a response window', '--option-1', 'A', '--option-2', 'B',
        '--resume-operation', 'review resume --request-id review-wait-request', '--response-timeout-ms', '1000'
    )
    $null = Invoke-Client $waitArgs
    $wait = Invoke-Client @('review', 'wait', '--user-root', $userRoot, '--request-id', 'review-wait-request', '--timeout-ms', '2000')
    Assert-True ($wait.ExitCode -eq 0 -and $wait.Json.awaitingHuman -eq $true -and $wait.Json.request.state -eq 'READY_AWAITING_HUMAN') 'review wait did not checkpoint successfully'
    $afterWait = Invoke-Client @('review', 'list', '--user-root', $userRoot)
    Assert-True ($afterWait.ExitCode -eq 0) 'review queue remained locked after timeout'

    $cancel = Invoke-Client @(
        'review', 'request', '--user-root', $userRoot, '--request-id', 'review-cancel-request', '--task-id', 'review-cancel-task',
        '--category', 'hard_blocker', '--question', 'Cancel this blocker', '--resume-operation', 'none'
    )
    Assert-True ($cancel.Json.request.state -eq 'WAITING_FOR_HUMAN') 'hard blocker request failed'
    $cancelled = Invoke-Client @('review', 'cancel', '--user-root', $userRoot, '--request-id', 'review-cancel-request')
    Assert-True ($cancelled.Json.request.state -eq 'CANCELLED') 'review cancellation failed'

    $expired = Invoke-Client @(
        'review', 'request', '--user-root', $userRoot, '--request-id', 'review-expired-request', '--task-id', 'review-expired-task',
        '--category', 'human_review', '--question', 'Expired review', '--option-1', 'A', '--option-2', 'B',
        '--resume-operation', 'none', '--expires-utc', ([DateTime]::UtcNow.AddSeconds(-1).ToString('o'))
    )
    $expiredList = Invoke-Client @('review', 'get', '--user-root', $userRoot, '--request-id', 'review-expired-request')
    Assert-True ($expiredList.Json.request.state -eq 'EXPIRED' -and $expiredList.Json.request.resourcesReleased -eq $true) 'expired review was not transitioned/released'

    Write-Output 'reviewQueue=PASS dedup=PASS checkpoint=PASS timeout=PASS resume=PASS cancel=PASS expired=PASS safety=PASS'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $userRoot) { Remove-Item -LiteralPath $userRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
