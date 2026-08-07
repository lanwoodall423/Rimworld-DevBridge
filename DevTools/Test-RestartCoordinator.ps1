param(
    [string]$CoordinatorPath = (Join-Path $PSScriptRoot '..\1.6\Assemblies\RestartCoordinator\net472\RimWorldDevBridge.RestartCoordinator.exe')
)

$ErrorActionPreference = 'Stop'
$CoordinatorPath = [IO.Path]::GetFullPath($CoordinatorPath)
if (-not (Test-Path -LiteralPath $CoordinatorPath -PathType Leaf)) { throw "Coordinator is missing: $CoordinatorPath" }

$root = Join-Path ([IO.Path]::GetTempPath()) ('DevBridgeCoordinatorTest-' + [Guid]::NewGuid().ToString('N'))
$userRoot = Join-Path $root 'user'
$coordinatorRoot = Join-Path $root 'coordinator'
[IO.Directory]::CreateDirectory($userRoot) | Out-Null
[IO.Directory]::CreateDirectory($coordinatorRoot) | Out-Null
$processIds = New-Object System.Collections.Generic.List[int]
$serverIds = New-Object System.Collections.Generic.List[int]
$clientIds = New-Object System.Collections.Generic.List[int]

function Start-CoordinatorServer([string]$root, [string]$path = $CoordinatorPath) {
    $server = Start-Process -FilePath $path -ArgumentList @('--serve', '--root', $root,
        '--user-root', $userRoot, '--bridge-root', (Split-Path -Parent $PSScriptRoot)) -WindowStyle Hidden -PassThru
    $serverIds.Add($server.Id)
    Start-Sleep -Milliseconds 250
}

function Invoke-Coordinator([string]$operation, [string[]]$arguments, [int]$expectedExit = 0) {
    $output = & $CoordinatorPath $operation '--root' $coordinatorRoot '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne $expectedExit) { throw "coordinator operation $operation exit expected=$expectedExit actual=$LASTEXITCODE output=$output" }
    try { return ($output.Trim() | ConvertFrom-Json) } catch { throw "coordinator operation $operation returned invalid JSON: $output" }
}

try {
    $game = (Get-Process -Id $PID).Path
    $working = Split-Path -Parent $game
    $arguments = '-NoProfile -Command "Start-Sleep -Seconds 30"'
    Start-CoordinatorServer $coordinatorRoot $CoordinatorPath
    $launch = Invoke-Coordinator 'launch' @(
        '--game-path', $game, '--working-directory', $working, '--arguments', $arguments,
        '--user-data-root', $userRoot, '--mod-configuration', 'managed-test',
        '--launch-profile', 'managed-test', '--owned'
    )
    if (-not $launch.Ok -or -not $launch.OwnershipJson) { throw 'launch did not return ownership' }
    $ownership = $launch.OwnershipJson | ConvertFrom-Json
    if (-not $ownership.Owned -or -not $ownership.Running -or $ownership.LaunchProfile -ne 'managed-test') { throw 'launch ownership projection was invalid' }
    $processIds.Add([int]$ownership.ProcessId)

    $requestArguments = @('--agent-id', 'coordinator-test-agent', '--package-id', 'Lan.RimWorldDevBridge',
        '--readiness', 'bridge', '--save-policy', 'none', '--reason', 'coordinator-test', '--timeout-ms', '1000')
    $first = Invoke-Coordinator 'ensure' ($requestArguments + @('--owned'))
    $second = Invoke-Coordinator 'ensure' ($requestArguments + @('--owned'))
    if ([string]::IsNullOrWhiteSpace($first.Ticket) -or $first.CycleId -ne $second.CycleId) {
        throw ('ensure did not coalesce compatible requests first={0} second={1}' -f ($first | ConvertTo-Json -Compress), ($second | ConvertTo-Json -Compress))
    }
    $status = Invoke-Coordinator 'status' @('--ticket', $first.Ticket)
    if (-not $status.OwnershipJson) { throw 'status did not preserve ownership projection' }

    $failedRoot = Join-Path $root 'failed-game'
    [IO.Directory]::CreateDirectory($failedRoot) | Out-Null
    Start-CoordinatorServer $failedRoot $CoordinatorPath
    $failedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('exit 7'))
    $failedArguments = "-NoProfile -EncodedCommand $failedCommand"
    $failedLaunch = & $CoordinatorPath launch '--root' $failedRoot '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--game-path' $game '--working-directory' $working '--arguments' $failedArguments '--user-data-root' $userRoot `
        '--mod-configuration' 'managed-test' '--launch-profile' 'managed-test' '--owned' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "failed-game launch failed: $failedLaunch" }
    $failedEnsure = & $CoordinatorPath ensure '--root' $failedRoot '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--agent-id' 'failed-game-agent' '--package-id' 'Lan.RimWorldDevBridge' '--readiness' 'bridge' '--save-policy' 'none' `
        '--timeout-ms' '3000' '--owned' 2>&1 | Out-String
    $failedEnsureJson = $failedEnsure.Trim() | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($failedEnsureJson.Ticket)) { throw "failed-game ensure returned no ticket: $failedEnsure" }
    $waitOnePath = Join-Path $failedRoot 'wait-one.json'
    $waitTwoPath = Join-Path $failedRoot 'wait-two.json'
    $waitOneError = Join-Path $failedRoot 'wait-one.err'
    $waitTwoError = Join-Path $failedRoot 'wait-two.err'
    $waitArguments = @('wait', '--root', $failedRoot, '--user-root', $userRoot,
        '--bridge-root', (Split-Path -Parent $PSScriptRoot), '--ticket', $failedEnsureJson.Ticket,
        '--timeout-ms', '10000')
    $waitOne = Start-Process -FilePath $CoordinatorPath -ArgumentList $waitArguments -RedirectStandardOutput $waitOnePath `
        -RedirectStandardError $waitOneError -WindowStyle Hidden -PassThru
    $waitTwo = Start-Process -FilePath $CoordinatorPath -ArgumentList $waitArguments -RedirectStandardOutput $waitTwoPath `
        -RedirectStandardError $waitTwoError -WindowStyle Hidden -PassThru
    $clientIds.Add($waitOne.Id)
    $clientIds.Add($waitTwo.Id)
    if (-not $waitOne.WaitForExit(30000) -or -not $waitTwo.WaitForExit(30000)) {
        throw 'concurrent failed-game waiters exceeded the bounded wait'
    }
    $failedWaitJson = @(
        (Get-Content -LiteralPath $waitOnePath -Raw | ConvertFrom-Json),
        (Get-Content -LiteralPath $waitTwoPath -Raw | ConvertFrom-Json)
    )
    foreach ($waitResult in $failedWaitJson) {
        if ($waitResult.Phase -ne 'FAILED' -or $waitResult.Error -ne 'managed_launch_failed' -or
            [string]$waitResult.Json -notmatch 'managed_process_exited_before_ready') {
            throw ('concurrent failed-game wait was not classified safely: {0}' -f ($waitResult | ConvertTo-Json -Compress))
        }
    }
    if ($failedWaitJson[0].Ticket -ne $failedWaitJson[1].Ticket) {
        throw 'concurrent failed-game waiters did not follow the same ticket'
    }
    $failedTicket = $failedWaitJson[0].Json | ConvertFrom-Json
    if ([string]$failedTicket.Completion -notmatch 'exitCode=7') {
        throw ('failed-game diagnostics omitted the observed exit code: {0}' -f ($failedWaitJson[0] | ConvertTo-Json -Compress))
    }

    $retryRoot = Join-Path $root 'retry-game'
    [IO.Directory]::CreateDirectory($retryRoot) | Out-Null
    Start-CoordinatorServer $retryRoot $CoordinatorPath
    $marker = Join-Path $retryRoot 'retry.marker'
    $retryScript = "`$marker = '$marker'; if (Test-Path -LiteralPath `$marker) { Start-Sleep -Seconds 30 } else { New-Item -ItemType File -Path `$marker | Out-Null; exit 7 }"
    $retryCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($retryScript))
    $retryArguments = "-NoProfile -EncodedCommand $retryCommand"
    $retryLaunch = & $CoordinatorPath launch '--root' $retryRoot '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--game-path' $game '--working-directory' $working '--arguments' $retryArguments '--user-data-root' $userRoot `
        '--mod-configuration' 'managed-test' '--launch-profile' 'managed-test' '--owned' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "retry launch failed: $retryLaunch" }
    $retryLaunchJson = $retryLaunch.Trim() | ConvertFrom-Json
    $firstRetryPid = [int]($retryLaunchJson.OwnershipJson | ConvertFrom-Json).ProcessId
    $processIds.Add($firstRetryPid)
    $retryEnsure = & $CoordinatorPath ensure '--root' $retryRoot '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--agent-id' 'retry-agent' '--package-id' 'Lan.RimWorldDevBridge' '--readiness' 'bridge' '--save-policy' 'none' `
        '--timeout-ms' '5000' '--max-launch-attempts' '2' '--launch-backoff-ms' '100' '--owned' 2>&1 | Out-String
    $retryEnsureJson = $retryEnsure.Trim() | ConvertFrom-Json
    $retryWait = & $CoordinatorPath wait '--root' $retryRoot '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--ticket' $retryEnsureJson.Ticket '--timeout-ms' '5000' 2>&1 | Out-String
    $retryWaitJson = $retryWait.Trim() | ConvertFrom-Json
    if ($retryWaitJson.Error -ne 'bridge_handshake_timeout') { throw "bounded retry did not reach handshake timeout: $retryWait" }
    $retryOwnership = $retryWaitJson.OwnershipJson | ConvertFrom-Json
    if (-not $retryOwnership.Running -or [int]$retryOwnership.ProcessId -eq $firstRetryPid) {
        throw "bounded retry did not replace the exited managed process: $retryWait"
    }
    $replacementPid = [int]$retryOwnership.ProcessId
    $processIds.Add($replacementPid)

    foreach ($serverId in @($serverIds)) { Stop-Process -Id $serverId -Force -ErrorAction SilentlyContinue }
    $serverIds.Clear()
    Start-CoordinatorServer $retryRoot $CoordinatorPath
    Stop-Process -Id $replacementPid -Force -ErrorAction SilentlyContinue
    $restartedWait = & $CoordinatorPath wait '--root' $retryRoot '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--ticket' $retryEnsureJson.Ticket '--timeout-ms' '5000' 2>&1 | Out-String
    $restartedWaitJson = $restartedWait.Trim() | ConvertFrom-Json
    if ($restartedWaitJson.Error -ne 'bridge_handshake_timeout' -or
        [int]($restartedWaitJson.OwnershipJson | ConvertFrom-Json).ProcessId -eq $replacementPid) {
        throw "coordinator restart did not recover stale managed ownership: $restartedWait"
    }
    $processIds.Add([int]($restartedWaitJson.OwnershipJson | ConvertFrom-Json).ProcessId)

    $staleOutput = Join-Path $root 'stale-output'
    [IO.Directory]::CreateDirectory($staleOutput) | Out-Null
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) { throw 'dotnet is required for stale coordinator replacement coverage' }
    $staleBuild = & $dotnet.Source build (Join-Path (Split-Path -Parent $PSScriptRoot) 'DevTools\RestartCoordinator\RimWorldDevBridge.RestartCoordinator.csproj') `
        '-c' 'Release' '--nologo' "/p:DevBridgeCoordinatorOutputRoot=$staleOutput\" `
        '/p:Version=1.0.0.1' '/p:AssemblyVersion=1.0.0.1' '/p:FileVersion=1.0.0.1' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "stale coordinator build failed: $staleBuild" }
    $stalePath = Join-Path $staleOutput 'RimWorldDevBridge.RestartCoordinator.exe'
    if (-not (Test-Path -LiteralPath $stalePath -PathType Leaf)) { throw 'stale coordinator output missing' }
    foreach ($serverId in @($serverIds)) { Stop-Process -Id $serverId -Force -ErrorAction SilentlyContinue }
    $staleCoordinatorRoot = $coordinatorRoot
    Start-CoordinatorServer $staleCoordinatorRoot $stalePath
    $client = Join-Path (Split-Path -Parent $PSScriptRoot) 'DevTools\devbridge.ps1'
    $expectedAssembly = [Reflection.AssemblyName]::GetAssemblyName($CoordinatorPath)
    $expectedHash = (Get-FileHash -LiteralPath $CoordinatorPath -Algorithm SHA256).Hash
    $expectedIdentity = "$($expectedAssembly.Name)|$($expectedAssembly.Version)|$expectedHash"
    $staleProbe = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $client restart status `
        '--bridge-root' (Split-Path -Parent $PSScriptRoot) '--user-root' $userRoot '--coordinator-path' $CoordinatorPath `
        '--ticket' 'stale-replacement-probe' '--json' 2>&1 | Out-String
    $staleProbeJson = $staleProbe.Trim() | ConvertFrom-Json
    if ($staleProbeJson.coordinator.serverIdentity -ne $expectedIdentity) {
        throw "stale coordinator was not replaced: $staleProbe"
    }

    $attachedRoot = Join-Path $root 'attached'
    [IO.Directory]::CreateDirectory($attachedRoot) | Out-Null
    $attachedCoordinator = Join-Path $attachedRoot 'coordinator'
    Start-CoordinatorServer $attachedCoordinator $CoordinatorPath
    $attachedRegister = & $CoordinatorPath register '--root' $attachedCoordinator '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--game-path' $game '--working-directory' $working '--arguments' $arguments '--user-data-root' $userRoot `
        '--mod-configuration' 'managed-test' '--launch-profile' 'managed-test' '--attached' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "attached registration failed: $attachedRegister" }
    $attached = & $CoordinatorPath ensure '--root' $attachedCoordinator '--user-root' $userRoot '--bridge-root' (Split-Path -Parent $PSScriptRoot) `
        '--agent-id' 'attached-test-agent' '--package-id' 'Lan.RimWorldDevBridge' '--readiness' 'bridge' '--save-policy' 'none' '--timeout-ms' '1000' 2>&1 | Out-String
    if ($LASTEXITCODE -ne 4) { throw "attached ensure exit expected=4 actual=$LASTEXITCODE output=$attached" }
    $attachedResult = $attached.Trim() | ConvertFrom-Json
    if ($attachedResult.Error -ne 'attached_live_process_requires_operator') { throw 'attached ensure did not fail closed' }

    Write-Output 'restartCoordinator=PASS launch=owned ensure=coalesced failed=classified stale=rotated attached=protected'
}
finally {
    foreach ($processId in $processIds) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
    foreach ($clientId in $clientIds) { Stop-Process -Id $clientId -Force -ErrorAction SilentlyContinue }
    foreach ($serverId in $serverIds) { Stop-Process -Id $serverId -Force -ErrorAction SilentlyContinue }
    try {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'RimWorldDevBridge.RestartCoordinator.exe' -and $_.CommandLine -like "*$root*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch { }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
