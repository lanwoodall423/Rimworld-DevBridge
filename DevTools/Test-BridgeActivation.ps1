param(
    [string]$BridgeRoot = (Split-Path -Parent $PSScriptRoot)
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
    if ($exitCode -ne $expectedExitCode) { throw "client exit expected=$expectedExitCode actual=$exitCode output=$($output -join ' ')" }
    $jsonLines = @($output | ForEach-Object { $_.ToString() } | Where-Object {
        $_.TrimStart().StartsWith('{') -or $_.TrimStart().StartsWith('[')
    })
    if ($jsonLines.Count -lt 1) { throw "client produced no JSON output=$($output -join ' ')" }
    return (($jsonLines[-1]) | ConvertFrom-Json)
}

function New-TestRoot([string]$prefix) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ($prefix + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    return $root
}

function Start-MockBridge([string]$root, [string]$coreFingerprint, [int]$wakeDelayMs) {
    $scriptPath = Join-Path $root 'mock-bridge.ps1'
    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
    $portProbe.Stop()
    $resetPath = Join-Path $root 'reset.request'
    $stalePath = Join-Path $root 'stale.request'
    $mock = @'
param([string]$UserRoot, [int]$Port, [int]$ProcessId, [string]$Mvid, [int]$WakeDelayMs, [string]$ResetPath, [string]$StalePath)
$ErrorActionPreference = 'Stop'
$statusPath = Join-Path $UserRoot 'RimWorld-DevBridge-Status.txt'
$wakePath = Join-Path $UserRoot 'RimWorld-DevBridge-Wake.request'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$listener.Start()
function Write-Status([string]$state, [bool]$stale = $false) {
    $token = if ($state -eq 'ON') { 'activation-token' } else { '' }
    $statusUtc = if ($stale) { [DateTime]::UtcNow.AddMinutes(-10).ToString('o') } else { [DateTime]::UtcNow.ToString('o') }
    $lines = @("bridge=$state", 'version=2.2.0', 'protocol=10', 'schema=v10.1-typed-core',
        ("processId={0}" -f $ProcessId), 'bootId=activation-boot', 'session=activation-session',
        'transportGeneration=1', 'host=127.0.0.1', ("port={0}" -f $Port), ("token={0}" -f $token),
        ("coreFingerprint={0}" -f $Mvid), ("statusUtc={0}" -f $statusUtc))
    [IO.File]::WriteAllLines($statusPath, $lines, [Text.UTF8Encoding]::new($false))
}
Write-Status 'DORMANT'
$wakeAt = $null
try {
    while ($true) {
        if (Test-Path -LiteralPath $ResetPath -PathType Leaf) {
            Remove-Item -LiteralPath $ResetPath -Force -ErrorAction SilentlyContinue
            Write-Status 'DORMANT' (Test-Path -LiteralPath $StalePath -PathType Leaf)
        }
        if (Test-Path -LiteralPath $wakePath -PathType Leaf) {
            Remove-Item -LiteralPath $wakePath -Force -ErrorAction SilentlyContinue
            $wakeAt = [DateTime]::UtcNow.AddMilliseconds($WakeDelayMs)
        }
        if ($wakeAt -and [DateTime]::UtcNow -ge $wakeAt) {
            $wakeAt = $null
            Remove-Item -LiteralPath $StalePath -Force -ErrorAction SilentlyContinue
            Write-Status 'ON'
        }
        if ($listener.Pending()) {
            $tcp = $listener.AcceptTcpClient()
            try {
                $stream = $tcp.GetStream()
                $reader = [IO.StreamReader]::new($stream)
                $line = $reader.ReadLine()
                $command = if ($line) { $line.Split('|')[2] } else { '' }
                $body = if ($command -eq 'AGENT_CONTEXT') {
                    '{"status":"OK","gameLoaded":false,"mapReady":false,"context":"activation"}'
                } else { '{"status":"OK","command":"' + $command + '"}' }
                $writer = [IO.StreamWriter]::new($stream)
                $writer.WriteLine($body)
                $writer.Flush()
                $writer.Dispose()
                $reader.Dispose()
                $stream.Dispose()
            } finally { $tcp.Dispose() }
        }
        Start-Sleep -Milliseconds 20
    }
} finally { $listener.Stop() }
'@
    Set-Content -LiteralPath $scriptPath -Value $mock -Encoding UTF8
    $server = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath,
        '-UserRoot', $root, '-Port', $port, '-ProcessId', $PID, '-Mvid', $coreFingerprint,
        '-WakeDelayMs', $wakeDelayMs, '-ResetPath', $resetPath, '-StalePath', $stalePath) -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 250
    return [pscustomobject]@{ Process = $server; Root = $root; ResetPath = $resetPath; StalePath = $stalePath; Port = $port }
}

function Get-ClientProcessResult([System.Diagnostics.Process]$process, [string]$stdoutPath, [int]$expectedExitCode) {
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = $process.ExitCode
    if ($null -eq $exitCode) { $exitCode = 0 }
    if ($exitCode -ne $expectedExitCode) {
        throw "concurrent client exit expected=$expectedExitCode actual=$exitCode stdout=$(Get-Content -LiteralPath $stdoutPath -Raw)"
    }
    return ((Get-Content -LiteralPath $stdoutPath -Raw).Trim() | ConvertFrom-Json)
}

$corePath = Join-Path $BridgeRoot '1.6/Assemblies/RimWorldDevBridge.dll'
$mvid = [Reflection.Assembly]::ReflectionOnlyLoadFrom($corePath).ManifestModule.ModuleVersionId.ToString('N')
$root = New-TestRoot 'DevBridgeActivation-'
$mock = $null
$timeoutRoot = $null
$timeoutMock = $null
try {
    $mock = Start-MockBridge $root $mvid 350
    $base = @('--bridge-root', $BridgeRoot, '--user-root', $root, '--startup-timeout-ms', '5000', '--progress-interval-ms', '100')

    $delayed = Invoke-Client (@('discover') + $base + @('--json'))
    if ($delayed.available -ne $true -or $delayed.activationRecovered -ne $true) {
        throw "delayed activation failed actual=$($delayed | ConvertTo-Json -Compress)"
    }

    Set-Content -LiteralPath $mock.ResetPath -Value '' -Encoding UTF8
    Start-Sleep -Milliseconds 100
    $readRetry = Invoke-Client (@('read', '--command=STATUS') + $base + @('--json'))
    if ($readRetry.status -ne 'OK') { throw "original read-only command was not retried actual=$($readRetry | ConvertTo-Json -Compress)" }

    Set-Content -LiteralPath $mock.ResetPath -Value '' -Encoding UTF8
    Start-Sleep -Milliseconds 100
    $outputOne = Join-Path $root 'client-one.json'
    $outputTwo = Join-Path $root 'client-two.json'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $client, 'discover') + $base + @('--json')
    $one = Start-Process powershell.exe -ArgumentList $arguments -RedirectStandardOutput $outputOne -WindowStyle Hidden -PassThru
    $two = Start-Process powershell.exe -ArgumentList $arguments -RedirectStandardOutput $outputTwo -WindowStyle Hidden -PassThru
    $oneResult = Get-ClientProcessResult $one $outputOne 0
    $twoResult = Get-ClientProcessResult $two $outputTwo 0
    if ($oneResult.available -ne $true -or $twoResult.available -ne $true) {
        throw "concurrent activation did not converge one=$($oneResult | ConvertTo-Json -Compress) two=$($twoResult | ConvertTo-Json -Compress)"
    }
    $state = Get-Content -LiteralPath (Join-Path $root 'RimWorld-DevBridge-Activation.json') -Raw | ConvertFrom-Json
    if ($state.state -ne 'ready' -or [string]::IsNullOrWhiteSpace($state.operationId)) { throw 'concurrent activation did not persist one completed cycle' }

    Set-Content -LiteralPath $mock.ResetPath -Value '' -Encoding UTF8
    Set-Content -LiteralPath $mock.StalePath -Value '' -Encoding UTF8
    Start-Sleep -Milliseconds 100
    $stale = Invoke-Client (@('discover') + $base + @('--json'))
    if ($stale.available -ne $true -or $stale.activationRecovered -ne $true) {
        throw "stale status recovery failed actual=$($stale | ConvertTo-Json -Compress)"
    }

    $timeoutRoot = New-TestRoot 'DevBridgeActivationTimeout-'
    $timeoutMock = Start-MockBridge $timeoutRoot $mvid 5000
    $timeout = Invoke-Client @('discover', '--bridge-root', $BridgeRoot, '--user-root', $timeoutRoot,
        '--startup-timeout-ms', '400', '--progress-interval-ms', '100', '--json') 4
    if ($timeout.reason -ne 'activation_timeout' -or $timeout.activation.state -ne 'activation_in_progress') {
        throw "activation timeout was not structured actual=$($timeout | ConvertTo-Json -Compress)"
    }
    Write-Output 'bridgeActivation=PASS delayed=PASS retry=PASS concurrent=PASS stale=PASS timeout=PASS'
}
finally {
    foreach ($item in @($mock, $timeoutMock)) {
        if ($item -and $item.Process) { Stop-Process -Id $item.Process.Id -Force -ErrorAction SilentlyContinue }
    }
    foreach ($path in @($root, $timeoutRoot)) {
        if ($path -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
