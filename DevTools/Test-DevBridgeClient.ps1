param(
    [string]$BridgeRoot = (Join-Path $PSScriptRoot '..'),
    [string]$UserRoot = $env:TEMP
)

$ErrorActionPreference = 'Stop'
$client = Join-Path $PSScriptRoot 'devbridge.ps1'
if (-not (Test-Path -LiteralPath $client -PathType Leaf)) { throw 'devbridge.ps1 is missing' }

function Invoke-Client([string[]]$arguments, [int]$expectedExitCode = 0) {
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $client @arguments 2>$null
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $expectedExitCode) {
        throw "client exit code expected=$expectedExitCode actual=$exitCode output=$($output -join ' ')"
    }
    if ($output.Count -lt 1) { throw 'client produced no JSON document' }
    return (($output -join [Environment]::NewLine) | ConvertFrom-Json)
}

$discovery = Invoke-Client @('discover', '--bridge-root', $BridgeRoot, '--user-root', $UserRoot, '--json')
if ($discovery.available -ne $false -or [string]::IsNullOrWhiteSpace($discovery.reason)) {
    throw "unavailable discovery result was not structured expected=available:false actual=$($discovery | ConvertTo-Json -Compress)"
}

$testUserRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridgeClient-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testUserRoot) | Out-Null
try {
    $restart = Invoke-Client @('restart', 'request', '--bridge-root', $BridgeRoot, '--user-root', $testUserRoot,
        '--agent-id', 'client-test-agent', '--package-id', 'client.test', '--reason', 'client test',
        '--readiness', 'bridge', '--save-policy', 'none', '--json')
    if ($restart.ok -ne $true -or [string]::IsNullOrWhiteSpace($restart.ticket)) {
        throw "durable restart request failed expected=opaque ticket actual=$($restart | ConvertTo-Json -Compress)"
    }
    $status = Invoke-Client @('restart', 'status', '--bridge-root', $BridgeRoot, '--user-root', $testUserRoot,
        '--ticket', $restart.ticket, '--json')
    if ($status.ticket -ne $restart.ticket -or [string]::IsNullOrWhiteSpace($status.phase)) {
        throw "durable restart status failed expected=ticket and phase actual=$($status | ConvertTo-Json -Compress)"
    }
    $wait = Invoke-Client @('restart', 'wait', '--bridge-root', $BridgeRoot, '--user-root', $testUserRoot,
        '--ticket', $restart.ticket, '--timeout-ms', '250', '--json') 4
    if ($wait.error -ne 'coordinator_wait_timeout' -or $wait.ticket -ne $restart.ticket) {
        throw "durable restart wait failed expected=timeout for ticket actual=$($wait | ConvertTo-Json -Compress)"
    }
    $unauthorized = Invoke-Client @('restart', 'request', '--bridge-root', $BridgeRoot, '--user-root', $testUserRoot,
        '--agent-id', 'client-live-agent', '--package-id', 'client.test', '--reason', 'live test',
        '--readiness', 'game', '--live-confirmed', '--save-policy', 'none', '--json')
    if ($unauthorized.ok -ne $true -or $unauthorized.phase -ne 'FAILED') {
        throw "live restart authorization was not rejected expected=FAILED actual=$($unauthorized | ConvertTo-Json -Compress)"
    }
}
finally {
    try {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'RimWorldDevBridge.RestartCoordinator.exe' -and $_.CommandLine -like "*$testUserRoot*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch { }
    if (Test-Path -LiteralPath $testUserRoot) { Remove-Item -LiteralPath $testUserRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output 'clientVerification=PASS unavailable-discovery=structured restart-ticket=durable live-restart=protected'
