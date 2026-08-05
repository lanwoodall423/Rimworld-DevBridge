param(
    [string]$BridgeRoot = (Join-Path $PSScriptRoot '..'),
    [string]$UserRoot = $env:TEMP
)

$ErrorActionPreference = 'Stop'
$client = Join-Path $PSScriptRoot 'devbridge.ps1'
if (-not (Test-Path -LiteralPath $client -PathType Leaf)) { throw 'devbridge.ps1 is missing' }

function Invoke-Client([string[]]$arguments, [int]$expectedExitCode = 0) {
    $oldErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $client @arguments 2>&1) }
    finally { $ErrorActionPreference = $oldErrorAction }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $expectedExitCode) {
        throw "client exit code expected=$expectedExitCode actual=$exitCode output=$($output -join ' ')"
    }
    $jsonLines = @($output | ForEach-Object { $_.ToString() } |
        Where-Object { $_.TrimStart().StartsWith('{') -or $_.TrimStart().StartsWith('[') })
    if ($jsonLines.Count -lt 1) { throw "client produced no JSON document output=$($output -join ' ')" }
    return (($jsonLines[-1]) | ConvertFrom-Json)
}

$discovery = Invoke-Client @('discover', '--bridge-root', $BridgeRoot, '--user-root', $UserRoot, '--json') 4
if ($discovery.available -ne $false -or [string]::IsNullOrWhiteSpace($discovery.reason)) {
    throw "unavailable discovery result was not structured expected=available:false actual=$($discovery | ConvertTo-Json -Compress)"
}

$identityRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridgeClientIdentity-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($identityRoot) | Out-Null
try {
    $identityOne = Invoke-Client @('discover', '--bridge-root', $BridgeRoot, '--user-root', $identityRoot, '--json') 4
    $identityTwo = Invoke-Client @('discover', '--bridge-root', $BridgeRoot, '--user-root', $identityRoot, '--json') 4
    if ([string]::IsNullOrWhiteSpace($identityOne.agentId) -or $identityOne.agentId -ne $identityTwo.agentId -or
        $identityTwo.agentIdPersisted -ne $true) {
        throw "stable agent identity failed expected=equal persisted IDs actual=$($identityOne.agentId)/$($identityTwo.agentId) persisted=$($identityTwo.agentIdPersisted)"
    }
    $override = Invoke-Client @('discover', '--bridge-root', $BridgeRoot, '--user-root', $identityRoot,
        '--agent-id', 'explicit-client-agent', '--json') 4
    if ($override.agentId -ne 'explicit-client-agent' -or $override.agentIdPersisted -ne $false) {
        throw "agent override failed expected=explicit-client-agent nonpersisted actual=$($override | ConvertTo-Json -Compress)"
    }
}
finally {
    if (Test-Path -LiteralPath $identityRoot) { Remove-Item -LiteralPath $identityRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

$invalidRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridgeClientInvalid-' + [Guid]::NewGuid().ToString('N'))
$invalid = Invoke-Client @('discover', '--bridge-root', $invalidRoot, '--user-root', $UserRoot, '--json') 2
if ($invalid.reason -ne 'client_error' -or [string]::IsNullOrWhiteSpace($invalid.detail)) {
    throw "path error serialization failed expected=client_error with detail actual=$($invalid | ConvertTo-Json -Compress)"
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

$mockRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevBridgeClientMock-' + [Guid]::NewGuid().ToString('N'))
$serverScript = Join-Path $mockRoot 'mock-server.ps1'
$serverLog = Join-Path $mockRoot 'requests.log'
[IO.Directory]::CreateDirectory($mockRoot) | Out-Null
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$mockPort = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
$portProbe.Stop()
$corePath = Join-Path $BridgeRoot '1.6\Assemblies\RimWorldDevBridge.dll'
$mockMvid = [Reflection.Assembly]::ReflectionOnlyLoadFrom($corePath).ManifestModule.ModuleVersionId.ToString('N')
$mockServer = @'
param([string]$UserRoot, [int]$Port, [int]$ProcessId, [string]$Mvid, [string]$LogPath)
$ErrorActionPreference = 'Stop'
$statusPath = Join-Path $UserRoot 'RimWorld-DevBridge-Status.txt'
$wakePath = Join-Path $UserRoot 'RimWorld-DevBridge-Wake.request'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$listener.Start()
function Write-Status([string]$state) {
    $token = if ($state -eq 'ON') { 'secret-token' } else { '' }
    $lines = @("bridge=$state", 'version=2.1.0', 'protocol=10', 'schema=v10.1-typed-core',
        ("processId={0}" -f $ProcessId), 'bootId=mock-boot', 'session=mock-session',
        'transportGeneration=1', 'host=127.0.0.1', ("port={0}" -f $Port), ("token={0}" -f $token),
        ("coreFingerprint={0}" -f $Mvid), ("statusUtc={0}" -f [DateTime]::UtcNow.ToString('o')))
    [IO.File]::WriteAllLines($statusPath, $lines, [Text.UTF8Encoding]::new($false))
}
Write-Status 'DORMANT'
try {
    while ($true) {
        if (Test-Path -LiteralPath $wakePath -PathType Leaf) {
            Remove-Item -LiteralPath $wakePath -Force -ErrorAction SilentlyContinue
            Write-Status 'ON'
        }
        if ($listener.Pending()) {
            $client = $listener.AcceptTcpClient()
            try {
                $reader = [IO.StreamReader]::new($client.GetStream())
                $line = $reader.ReadLine()
                Add-Content -LiteralPath $LogPath -Value $line
                $parts = $line.Split('|')
                $command = if ($parts.Count -gt 2) { $parts[2] } else { '' }
                $body = switch ($command) {
                    'WRITE_LEASE' { '{"status":"OK","lease":"secret-token","context":"sandbox","expiresUtc":"2099-01-01T00:00:00Z"}' }
                    'RENEW_WRITE_LEASE' { '{"status":"OK","lease":"secret-token","context":"sandbox","expiresUtc":"2099-01-01T00:00:00Z"}' }
                    'REVOKE_WRITE_LEASE' { '{"status":"OK","lease":"secret-token"}' }
                    default { '{"status":"OK","command":"' + $command + '"}' }
                }
                $writer = [IO.StreamWriter]::new($client.GetStream())
                $writer.WriteLine($body)
                $writer.Flush()
                $writer.Dispose()
                $reader.Dispose()
            }
            finally { $client.Dispose() }
        }
        Start-Sleep -Milliseconds 20
    }
}
finally { $listener.Stop() }
'@
Set-Content -LiteralPath $serverScript -Value $mockServer -Encoding UTF8
$server = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $serverScript,
    '-UserRoot', $mockRoot, '-Port', $mockPort, '-ProcessId', $PID, '-Mvid', $mockMvid, '-LogPath', $serverLog) -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Milliseconds 250
    $wake = Invoke-Client @('wake', '--bridge-root', $BridgeRoot, '--user-root', $mockRoot,
        '--timeout-ms', '5000', '--json')
    if ($wake.available -ne $true -or $wake.woke -ne $true) {
        throw "wake failed expected=available and woke actual=$($wake | ConvertTo-Json -Compress)"
    }
    $lease = Invoke-Client @('lease', 'acquire', '--context', 'sandbox', '--bridge-root', $BridgeRoot,
        '--user-root', $mockRoot, '--json')
    $leaseJson = $lease | ConvertTo-Json -Compress
    if ($lease.status -ne 'OK' -or $lease.lease -ne '[REDACTED]' -or $leaseJson.Contains('secret-token')) {
        throw "lease redaction failed expected=OK without token actual=$leaseJson"
    }
    $renew = Invoke-Client @('lease', 'renew', '--bridge-root', $BridgeRoot, '--user-root', $mockRoot, '--json')
    if ($renew.status -ne 'OK' -or $renew.lease -ne '[REDACTED]') {
        throw "lease reuse failed expected=stored lease reuse actual=$($renew | ConvertTo-Json -Compress)"
    }
    $firstRetry = Invoke-Client @('mutate', '--command=SET_SPEED', '--argument=1',
        '--idempotency-key', 'client-retry-key', '--bridge-root', $BridgeRoot, '--user-root', $mockRoot, '--json')
    $secondRetry = Invoke-Client @('mutate', '--command=SET_SPEED', '--argument=1',
        '--idempotency-key', 'client-retry-key', '--bridge-root', $BridgeRoot, '--user-root', $mockRoot, '--json')
    if ($firstRetry.idempotencyKey -ne 'client-retry-key' -or $secondRetry.idempotencyKey -ne 'client-retry-key') {
        throw "idempotency key propagation failed expected=client-retry-key actual=$($firstRetry | ConvertTo-Json -Compress)"
    }
    $release = Invoke-Client @('lease', 'release', '--bridge-root', $BridgeRoot, '--user-root', $mockRoot, '--json')
    if ($release.status -ne 'OK') { throw "lease release failed expected=OK actual=$($release | ConvertTo-Json -Compress)" }
    $logged = [IO.File]::ReadAllText($serverLog)
    if (($logged.Split([Environment]::NewLine) | Where-Object { $_ -match 'idempotency=client-retry-key' }).Count -ne 2) {
        throw 'idempotent retry did not send the supplied key on both attempts'
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $mockRoot) { Remove-Item -LiteralPath $mockRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output 'clientVerification=PASS unavailable-discovery=structured restart-ticket=durable live-restart=protected'
