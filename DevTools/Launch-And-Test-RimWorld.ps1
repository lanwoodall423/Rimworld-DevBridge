param(
    [string]$Command = "STATUS",
    [string]$Argument = "",
    [string]$GamePath = "",
    [string[]]$GameArguments = @(),
    [int]$StartupTimeoutSeconds = 300,
    [int]$BridgeWakeTimeoutSeconds = 10,
    [int]$CommandTimeoutMs = 120000,
    [string]$UserRoot = "",
    [string]$ModConfiguration = "managed-test",
    [string]$Layout = "auto",
    [string]$GameProcessName = "RimWorldWin64",
    [switch]$RequireMap,
    [switch]$SkipBuild,
    [switch]$NoQuickTest,
    [switch]$KeepRunning,
    [switch]$ForceKillTestOnly
)

$ErrorActionPreference = "Stop"
$modRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$gameRoot = [IO.Path]::GetFullPath((Join-Path $modRoot "..\.."))
$saveDir = if (-not [string]::IsNullOrWhiteSpace($UserRoot)) { $UserRoot } elseif (-not [string]::IsNullOrWhiteSpace($env:RIMWORLD_DEVBRIDGE_USER_ROOT)) { $env:RIMWORLD_DEVBRIDGE_USER_ROOT } else {
    Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
}
$saveDir = [IO.Path]::GetFullPath($saveDir)
if (-not (Test-Path -LiteralPath $saveDir -PathType Container)) { throw "UserRoot does not exist: $saveDir" }
$statusPath = Join-Path $saveDir "RimWorld-DevBridge-Status.txt"
$wakePath = Join-Path $saveDir "RimWorld-DevBridge-Wake.request"
$inputPath = Join-Path $saveDir "RimWorld-DevBridge-In.txt"
$outputPath = Join-Path $saveDir "RimWorld-DevBridge-Out.txt"
$playerLog = Join-Path $saveDir "Player.log"
$manifestPath = Join-Path $modRoot "BRIDGE_MANIFEST.txt"
$clientPath = Join-Path $PSScriptRoot "devbridge.ps1"
$projectPath = Join-Path $modRoot "Source\RimWorldDevBridge\RimWorldDevBridge.csproj"
$logRoot = Join-Path $saveDir "RimWorldDevBridge\LauncherLogs"
$stamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
$stdoutLog = Join-Path $logRoot "rimworld-$stamp.stdout.log"
$stderrLog = Join-Path $logRoot "rimworld-$stamp.stderr.log"
$buildLog = Join-Path $logRoot "rimworld-$stamp.build.log"
$ownedProcess = $false
$ownedBootId = ""
$ownedArtifactsRemoved = $false
$process = $null
. (Join-Path $PSScriptRoot "RimWorldLauncherSupport.ps1")

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

function Write-LogTail([string]$Label, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Write-Output ("{0}={1}" -f $Label, $Path)
    Get-Content -LiteralPath $Path -Tail 25 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Output ("{0}> {1}" -f $Label, $_) }
}

function Stop-OwnedProcess {
    if (-not $ownedProcess -or $null -eq $process) { return }
    $process.Refresh()
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        try { $process.WaitForExit(10000) | Out-Null } catch { }
        if (-not $process.HasExited -and $ForceKillTestOnly) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            try { $process.WaitForExit(10000) | Out-Null } catch { }
        }
    }
    $script:ownedArtifactsRemoved = Remove-OwnedBridgeArtifacts -ProcessId $process.Id -BootId $ownedBootId `
        -StatusPath $statusPath -ArtifactPaths @($statusPath, $wakePath, $inputPath, $outputPath)
}

function Fail([int]$Code, [string]$Message) {
    Write-Output ("summary=FAIL reason={0}" -f $Message)
    Write-Output ("processId={0} owned={1}" -f $(if ($process) { $process.Id } else { 0 }), $ownedProcess)
    Write-LogTail "stdoutLog" $stdoutLog
    Write-LogTail "stderrLog" $stderrLog
    Write-LogTail "playerLog" $playerLog
    if ($ownedProcess -and -not $KeepRunning) { Stop-OwnedProcess }
    exit $Code
}

function Assert-ProcessRunning {
    if ($null -eq $process) { Fail 2 "rimworld_process_missing" }
    $process.Refresh()
    if ($process.HasExited) {
        try { $process.WaitForExit(); $code = $process.ExitCode }
        catch { $code = "unknown" }
        if ($null -eq $code -or "$code".Length -eq 0) { $code = "unknown" }
        Fail 2 ("rimworld_exited_{0}" -f $code)
    }
}

function Invoke-Bridge([string]$Name, [string]$Value, [string]$Options = "") {
    $shellPath = (Get-Process -Id $PID).Path
    $clientArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $clientPath,
        "call", "--command=$Name", "--bridge-root=$modRoot", "--user-root=$saveDir",
        "--timeout-ms=$CommandTimeoutMs", "--json")
    if (-not [string]::IsNullOrEmpty($Value)) { $clientArguments += "--argument=$Value" }
    if (-not [string]::IsNullOrEmpty($Options)) {
        foreach ($option in ($Options -split '&')) {
            if (-not [string]::IsNullOrWhiteSpace($option)) { $clientArguments += "--option=$option" }
        }
    }
    $lines = & $shellPath $clientArguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $lines | ForEach-Object { Write-Output $_ }
        Fail 4 ("bridge_client_exit_{0}" -f $exitCode)
    }
    $json = @($lines | ForEach-Object { "$($_)" } | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
    if ($json.Count -eq 0) { Fail 4 "bridge_client_invalid_json" }
    try { return ($json[0] | ConvertFrom-Json) }
    catch { Fail 4 "bridge_client_invalid_json" }
}

function Response-Value($Lines, [string]$Name) {
    if ($null -ne $Lines -and $Lines.PSObject.Properties[$Name]) {
        return [string]$Lines.PSObject.Properties[$Name].Value
    }
    $prefix = $Name + "="
    $line = $Lines | Where-Object { "$_".StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -eq $line) { return $null }
    return "$line".Substring($prefix.Length)
}

function Start-CoordinatorOwnedProcess([string]$Executable, [string[]]$Arguments) {
    $devbridgePath = Join-Path $PSScriptRoot "devbridge.ps1"
    $shellPath = (Get-Process -Id $PID).Path
    $argumentText = ($Arguments | ForEach-Object {
        if ([string]::IsNullOrEmpty($_)) { '""' }
        elseif ($_ -match '[\s"]') { '"' + $_.Replace('"', '\\"') + '"' }
        else { $_ }
    }) -join ' '
    $clientArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $devbridgePath,
        "restart", "launch", "--bridge-root", $modRoot, "--user-root", $saveDir,
        "--game-path", $Executable, "--working-directory", (Split-Path -Parent $Executable),
        "--arguments", $argumentText, "--mod-configuration", $ModConfiguration,
        "--user-data-root", $saveDir,
        "--launch-profile", "managed-test", "--layout", $Layout)
    $lines = & $shellPath $clientArguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $lines | ForEach-Object { Write-Output $_ }
        throw "coordinator-owned launch failed"
    }
    $json = (@($lines) -join "`n") | ConvertFrom-Json
    $pidValue = $json.ownership.ProcessId
    if ($null -eq $pidValue) { throw "coordinator launch did not return a process id" }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        try { return Get-Process -Id ([int]$pidValue) -ErrorAction Stop }
        catch { Start-Sleep -Milliseconds 100 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "coordinator-owned process did not remain running"
}

if ($StartupTimeoutSeconds -lt 1 -or $StartupTimeoutSeconds -gt 1800) {
    throw "StartupTimeoutSeconds must be between 1 and 1800."
}
if ($BridgeWakeTimeoutSeconds -lt 1 -or $BridgeWakeTimeoutSeconds -gt 60) {
    throw "BridgeWakeTimeoutSeconds must be between 1 and 60."
}
if ($CommandTimeoutMs -lt 50 -or $CommandTimeoutMs -gt 120000) {
    throw "CommandTimeoutMs must be between 50 and 120000."
}
[IO.Directory]::CreateDirectory($logRoot) | Out-Null
$manifest = Read-KeyFile $manifestPath
foreach ($required in @("bridge", "protocol", "schema")) {
    if (-not $manifest.ContainsKey($required)) { throw "Bridge manifest is missing '$required'." }
}

$process = Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($process) {
    $attachedStatus = Read-KeyFile $statusPath
    if ($attachedStatus["processId"] -ne "$($process.Id)" -or [string]::IsNullOrWhiteSpace($attachedStatus["bootId"])) {
        Write-Output '{"ok":false,"status":"USER_RESTART_REQUIRED","ownership":"attached","operatorActionRequired":true,"nextAction":"stop the manually attached RimWorld process, then rerun"}'
        exit 4
    }
    if ($Command.ToUpperInvariant() -eq "RUN_FEATURE_TESTS") {
        Write-Output '{"ok":false,"status":"ATTACHED_READ_ONLY","ownership":"attached","operatorActionRequired":true,"nextAction":"use restart ensure for a coordinator-owned test process"}'
        exit 4
    }
    Write-Output ("launch=ATTACHED processId={0}" -f $process.Id)
}
else {
    if ([string]::IsNullOrWhiteSpace($GamePath)) { $GamePath = Join-Path $gameRoot "RimWorldWin64.exe" }
    $GamePath = [IO.Path]::GetFullPath($GamePath)
    if (-not (Test-Path -LiteralPath $GamePath -PathType Leaf)) { throw "RimWorld executable not found: $GamePath" }
    if (-not $SkipBuild) {
        & dotnet build $projectPath -c Release --nologo --verbosity quiet *> $buildLog
        if ($LASTEXITCODE -ne 0) {
            Get-Content -LiteralPath $buildLog -Tail 40 -ErrorAction SilentlyContinue
            exit 1
        }
        Write-Output ("build=PASS log={0}" -f $buildLog)
    }
    $arguments = @()
    if (-not $NoQuickTest) { $arguments += "-quicktest" }
    $arguments += $GameArguments
    $process = Start-CoordinatorOwnedProcess $GamePath $arguments
    $ownedProcess = $true
    Write-Output ("launch=STARTED processId={0} arguments={1}" -f $process.Id, ($arguments -join " "))
}

$deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
$status = @{}
do {
    Assert-ProcessRunning
    $status = Read-KeyFile $statusPath
    if ($status["processId"] -eq "$($process.Id)") { break }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $deadline)
if ($status["processId"] -ne "$($process.Id)") { Fail 3 "bridge_status_timeout" }
if ($ownedProcess) { $ownedBootId = $status["bootId"] }

if ($status["version"] -ne $manifest["bridge"] -or $status["protocol"] -ne $manifest["protocol"] -or
    $status["schema"] -ne $manifest["schema"]) {
    Fail 3 ("bridge_manifest_mismatch_loaded_{0}_{1}_{2}_disk_{3}_{4}_{5}" -f
        $status["version"], $status["protocol"], $status["schema"],
        $manifest["bridge"], $manifest["protocol"], $manifest["schema"])
}

[IO.File]::WriteAllText($wakePath, "")
$wakeDeadline = if ($ownedProcess) { $deadline } else {
    [DateTime]::UtcNow.AddSeconds($BridgeWakeTimeoutSeconds)
}
do {
    Assert-ProcessRunning
    $status = Read-KeyFile $statusPath
    if ($status["processId"] -eq "$($process.Id)" -and $status["bridge"] -eq "ON") { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $wakeDeadline)
if ($status["bridge"] -ne "ON") {
    $activation = if ($status.ContainsKey("activationError")) { $status["activationError"] } else { "none" }
    Fail 3 ("bridge_wake_timeout_activation_{0}" -f $activation)
}
Write-Output ("bridge=READY version={0} protocol={1} schema={2}" -f
    $status["version"], $status["protocol"], $status["schema"])

$mustHaveMap = $RequireMap -or $Command.ToUpperInvariant() -eq "RUN_FEATURE_TESTS"
do {
    Assert-ProcessRunning
    $session = Invoke-Bridge "SESSION" ""
    $gameLoaded = Response-Value $session "gameLoaded"
    $mapLoaded = Response-Value $session "mapLoaded"
    if (-not $mustHaveMap -or $mapLoaded -eq "True") { break }
    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $deadline)
if ($mustHaveMap -and $mapLoaded -ne "True") { Fail 3 "quicktest_map_timeout" }
Write-Output ("game=READY gameLoaded={0} mapLoaded={1}" -f $gameLoaded, $mapLoaded)

$options = "allowExpensive=true"
if ($Command.ToUpperInvariant() -eq "RUN_FEATURE_TESTS") {
    $leaseResponse = Invoke-Bridge "WRITE_LEASE" "sandbox"
    $lease = Response-Value $leaseResponse "lease"
    if ([string]::IsNullOrWhiteSpace($lease)) { Fail 4 "write_lease_missing" }
    $options += "&lease=$lease&idempotency=$([Guid]::NewGuid().ToString('N'))"
}
$response = Invoke-Bridge $Command $Argument $options
$response | ConvertTo-Json -Depth 8 -Compress | Write-Output
$resultStatus = Response-Value $response "status"
if ($resultStatus -ne "OK") { Fail 5 ("command_status_{0}" -f $resultStatus) }

Write-Output ("summary=PASS command={0} processId={1} owned={2}" -f
    $Command.ToUpperInvariant(), $process.Id, $ownedProcess)
Write-Output ("playerLog={0}" -f $playerLog)
if ($ownedProcess -and -not $KeepRunning) {
    Stop-OwnedProcess
    Write-Output "cleanup=STOPPED_OWNED_PROCESS"
    Write-Output ("cleanupArtifacts={0}" -f $(if ($ownedArtifactsRemoved) { "REMOVED" } else { "UNCHANGED" }))
}
elseif ($ownedProcess) { Write-Output "cleanup=KEPT_RUNNING" }
else { Write-Output "cleanup=ATTACHED_PROCESS_UNCHANGED" }
