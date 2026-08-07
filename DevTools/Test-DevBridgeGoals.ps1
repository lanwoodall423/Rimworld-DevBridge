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
