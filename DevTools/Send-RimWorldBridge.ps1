param(
    [Parameter(Position=0)][string]$Command = "SYNC",
    [Parameter(Position=1)][string]$Argument = "",
    [int]$TimeoutMs = 5000,
    [string]$Options = "",
    [switch]$NoExitOnRestartRequired
)

$ErrorActionPreference = "Stop"

$saveDir = Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
$statusPath = Join-Path $saveDir "RimWorld-DevBridge-Status.txt"
$wakePath = Join-Path $saveDir "RimWorld-DevBridge-Wake.request"
$manifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) "BRIDGE_MANIFEST.txt"

function Read-KeyFile([string]$Path) {
    $values = @{}
    try {
        if (Test-Path -LiteralPath $Path) {
            foreach ($line in [IO.File]::ReadAllLines($Path)) {
                $split = $line.IndexOf("=")
                if ($split -gt 0) { $values[$line.Substring(0, $split)] = $line.Substring($split + 1) }
            }
        }
    }
    catch [IO.IOException] { }
    return $values
}

function Assert-BridgeCurrent($Status, $Manifest) {
    if (-not $Status.ContainsKey("version") -or -not $Manifest.ContainsKey("bridge")) { return }
    $loadedProtocol = "$($Status["protocol"])" -replace "^v", ""
    if ($Status["version"] -ne $Manifest["bridge"] -or
        $loadedProtocol -ne $Manifest["protocol"] -or
        ($Status.ContainsKey("schema") -and $Status["schema"] -ne $Manifest["schema"])) {
        Write-Output "status=RESTART_REQUIRED"
        Write-Output ("loaded=version:{0} protocol:{1} schema:{2}" -f
            $Status["version"], $loadedProtocol, $Status["schema"])
        Write-Output ("disk=version:{0} protocol:{1} schema:{2}" -f
            $Manifest["bridge"], $Manifest["protocol"], $Manifest["schema"])
        if ($NoExitOnRestartRequired) { throw "Restart RimWorld to load the bridge version on disk." }
        exit 5
    }
}

$manifest = Read-KeyFile $manifestPath
$status = Read-KeyFile $statusPath
Assert-BridgeCurrent $status $manifest

if ($status.ContainsKey("processId")) {
    $running = Get-Process -Id ([int]$status["processId"]) -ErrorAction SilentlyContinue
    if (-not $running) { $status = @{} }
}

if ($TimeoutMs -lt 50 -or $TimeoutMs -gt 120000) {
    throw "TimeoutMs must be between 50 and 120000."
}
if ($Options -notmatch '(^|&)timeoutMs=') {
    $Options = (($Options.Trim('&') + "&timeoutMs=$TimeoutMs").Trim('&'))
}
if ($status["bridge"] -ne "ON") {
    [IO.File]::WriteAllText($wakePath, "")
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        Start-Sleep -Milliseconds 40
        $status = Read-KeyFile $statusPath
    } while ($status["bridge"] -ne "ON" -and [DateTime]::UtcNow -lt $deadline)
}

if ($status["bridge"] -ne "ON") {
    throw "Bridge did not wake. Start RimWorld and enable RimWorld Dev Bridge."
}
Assert-BridgeCurrent $status $manifest

$client = [Net.Sockets.TcpClient]::new()
try {
    $client.ReceiveTimeout = $TimeoutMs
    $client.SendTimeout = $TimeoutMs
    $connect = $client.BeginConnect($status["host"], [int]$status["port"], $null, $null)
    if (-not $connect.AsyncWaitHandle.WaitOne($TimeoutMs)) {
        Write-Output "id=unknown"
        Write-Output "status=TIMEOUT"
        Write-Output "error=connection_timeout"
        exit 4
    }
    $client.EndConnect($connect)
    $stream = $client.GetStream()
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
    $writer.AutoFlush = $true
    $id = [Guid]::NewGuid().ToString("N")
    $writer.WriteLine($status["token"] + "|" + $id + "|" + $Command.ToUpperInvariant() + "|" +
        $Argument + "|" + $Options)
    $response = $reader.ReadToEnd()
    $response
}
catch [IO.IOException] {
    Write-Output "id=unknown"
    Write-Output "status=TIMEOUT"
    Write-Output "error=connection_or_response_timeout"
    exit 4
}
catch [Net.Sockets.SocketException] {
    Write-Output "id=unknown"
    Write-Output "status=UNAVAILABLE"
    Write-Output "error=connection_failed"
    Write-Output ("detail=" + $_.Exception.Message.Replace("`r", " ").Replace("`n", " "))
    exit 4
}
finally {
    if ($connect -and $connect.AsyncWaitHandle) { $connect.AsyncWaitHandle.Dispose() }
    if ($writer) { $writer.Dispose() }
    if ($reader) { $reader.Dispose() }
    if ($stream) { $stream.Dispose() }
    $client.Dispose()
}
