param(
    [string] $BridgeRoot = (Split-Path -Parent $PSScriptRoot),
    [string] $UserRoot
)

$ErrorActionPreference = 'Stop'
$client = Join-Path $BridgeRoot 'DevTools\devbridge.ps1'
if ([string]::IsNullOrWhiteSpace($UserRoot)) { $UserRoot = Join-Path $env:TEMP ('RimWorldDevBridgeGoalTest-' + [Guid]::NewGuid().ToString('N')) }
[IO.Directory]::CreateDirectory($UserRoot) | Out-Null

function Assert-Goal([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}

function Invoke-GoalClient([string[]] $arguments, [int] $expectedExit = 0) {
    $all = @('goal') + $arguments + @('--bridge-root', $BridgeRoot, '--user-root', $UserRoot, '--json')
    $oldErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $client @all 2>$null | Out-String }
    finally { $ErrorActionPreference = $oldErrorAction }
    $jsonLines = @($output -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('{') })
    if ($jsonLines.Count -eq 0) { throw "goal client returned no JSON: $output" }
    $result = $jsonLines[$jsonLines.Count - 1] | ConvertFrom-Json
    return $result
}

$goalId = 'goal-test-' + [Guid]::NewGuid().ToString('N')
try {
    $invalid = Invoke-GoalClient @('ensure', '--goal-id=bad goal', '--desired-state=test_ready') 2
    Assert-Goal ([string]$invalid.reason -eq 'client_error' -and [string]$invalid.detail -eq 'goal_id_invalid') 'invalid goal ID was accepted'

    $completedGoalId = 'goal-completed-' + [Guid]::NewGuid().ToString('N')
    $completedPath = Join-Path $UserRoot 'RimWorld-DevBridge-Goals'
    [IO.Directory]::CreateDirectory($completedPath) | Out-Null
    $completedState = [ordered]@{
        schema = 1
        operationKind = 'runtime_goal'
        goalId = $completedGoalId
        operationId = 'goal-completed-operation'
        packageId = 'Lan.RimWorldDevBridge'
        desiredState = 'bridge'
        operationState = 'succeeded'
        phase = 'READY'
        code = 'bridge_ready'
        startedUtc = [DateTime]::UtcNow.ToString('o')
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        progressSequence = 4
        pid = 1234
        sessionId = 'completed-session'
        lifecycleGeneration = 7
        coreFingerprint = 'completed-core'
        contextFresh = $true
        recoverable = $false
        requiredAction = 'none'
        waitFor = 'none'
        keepRunning = $true
        retrySafe = $true
        operatorActionRequired = $false
        nextAction = 'none'
        resourcesReleased = $false
        evidence = @([ordered]@{ phase = 'READY'; processId = 1234 })
    }
    [IO.File]::WriteAllText((Join-Path $completedPath ("goal-{0}.json" -f $completedGoalId)), ($completedState | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8)
    $completedResume = Invoke-GoalClient @('resume', "--goal-id=$completedGoalId") 0
    Assert-Goal ([string]$completedResume.operationState -eq 'succeeded' -and [string]$completedResume.operationId -eq 'goal-completed-operation' -and $completedResume.ok -eq $true) 'resume reran a terminal succeeded goal'

    $checkpointedGoalId = 'goal-checkpointed-' + [Guid]::NewGuid().ToString('N')
    $checkpointedState = [ordered]@{}
    foreach ($entry in $completedState.GetEnumerator()) { $checkpointedState[$entry.Key] = $entry.Value }
    $checkpointedState.goalId = $checkpointedGoalId
    $checkpointedState.operationId = 'goal-checkpointed-operation'
    $checkpointedState.desiredState = 'bridge'
    $checkpointedState.operationState = 'checkpointed'
    $checkpointedState.phase = 'READY_AWAITING_HUMAN'
    $checkpointedState.code = 'goal_checkpointed'
    $checkpointedState.resourcesReleased = $true
    [IO.File]::WriteAllText((Join-Path $completedPath ("goal-{0}.json" -f $checkpointedGoalId)), ($checkpointedState | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8)
    $checkpointedResume = Invoke-GoalClient @('resume', "--goal-id=$checkpointedGoalId") 3
    Assert-Goal ([string]$checkpointedResume.desiredState -eq 'bridge') 'resume changed the persisted desired postcondition'

    $ensure = Invoke-GoalClient @(
        'ensure', "--goal-id=$goalId", '--desired-state=test_ready', '--timeout-ms=3000',
        '--no-progress-timeout-ms=1000', ("--game-path={0}" -f $env:ComSpec),
        ("--working-directory={0}" -f $env:SystemRoot), '--arguments=/c exit 0', '--mod-configuration=managed-test'
    ) 3
    Assert-Goal ([string]$ensure.goalId -eq $goalId) ("goal ID was not persisted: " + ($ensure | ConvertTo-Json -Depth 10 -Compress))
    Assert-Goal (-not [string]::IsNullOrWhiteSpace([string]$ensure.operationId)) 'operation ID was not persisted'
    Assert-Goal ([string]$ensure.operationState -eq 'failed') 'authorization failure was not terminal'
    Assert-Goal ([string]$ensure.code -eq 'sandbox_authorization_missing') 'authorization failure code was not stable'
    Assert-Goal ($ensure.recoverable -eq $true -and $ensure.retrySafe -eq $true) 'goal recovery fields were incomplete'
    Assert-Goal ($ensure.operatorActionRequired -eq $false) 'missing managed authorization incorrectly required an operator'
    $persistedEnsure = Get-Content -LiteralPath (Join-Path $completedPath ("goal-{0}.json" -f $goalId)) -Raw | ConvertFrom-Json
    Assert-Goal ([int]$persistedEnsure.timeoutMs -eq 3000 -and [int]$persistedEnsure.noProgressTimeoutMs -eq 1000) 'goal timeout policy was not persisted from the request'

    $duplicate = Invoke-GoalClient @('ensure', "--goal-id=$goalId", '--desired-state=test_ready') 3
    Assert-Goal ([string]$duplicate.operationId -eq [string]$ensure.operationId) 'duplicate goal created a new operation'

    $status = Invoke-GoalClient @('status', "--goal-id=$goalId") 3
    Assert-Goal ([string]$status.operationId -eq [string]$ensure.operationId) 'status lost durable operation identity'

    $checkpoint = Invoke-GoalClient @('checkpoint', "--goal-id=$goalId") 0
    Assert-Goal ([string]$checkpoint.operationState -eq 'checkpointed') 'checkpoint did not persist'
    Assert-Goal ($checkpoint.resourcesReleased -eq $true) 'checkpoint did not release resources'
    Assert-Goal ([string]$checkpoint.nextAction -like 'goal resume*') 'checkpoint did not preserve resume action'

    $resumed = Invoke-GoalClient @('resume', "--goal-id=$goalId", '--desired-state=test_ready', '--timeout-ms=3000') 3
    Assert-Goal ([string]$resumed.operationId -eq [string]$ensure.operationId) 'resume changed durable operation identity'
    Assert-Goal ([string]$resumed.operationState -eq 'failed') 'resume did not return a terminal safe result'

    $cancel = Invoke-GoalClient @('cancel', "--goal-id=$goalId") 0
    Assert-Goal ([string]$cancel.operationState -eq 'cancelled') 'cancel did not persist terminal state'
    Assert-Goal ($cancel.resourcesReleased -eq $true) 'cancel did not release resources'
    Assert-Goal ($cancel.operatorActionRequired -eq $false) 'cancel granted operator authority'
    Write-Output 'goalOrchestration=PASS persistence=PASS dedup=PASS checkpoint=PASS resume=PASS cancel=PASS safety=PASS'
}
finally {
    if (Test-Path -LiteralPath $UserRoot) { Remove-Item -LiteralPath $UserRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
