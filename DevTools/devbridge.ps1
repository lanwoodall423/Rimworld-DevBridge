param(
    [Parameter(Position = 0)] [string] $Operation = "discover",
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $CommandArguments
)

$ErrorActionPreference = "Stop"

function Get-Value([string[]] $values, [string] $name, [string] $default = $null) {
    for ($index = 0; $index -lt $values.Count; $index++) {
        if ($values[$index] -eq $name -and $index + 1 -lt $values.Count) { return $values[$index + 1] }
    }
    return $default
}

function Has-Flag([string[]] $values, [string] $name) {
    return $values -contains $name
}

function Write-JsonResult($value) {
    $value | ConvertTo-Json -Depth 12 -Compress
}

function Read-KeyFile([string] $path) {
    $values = [ordered]@{}
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $values }
    try {
        foreach ($line in [IO.File]::ReadAllLines($path)) {
            $separator = $line.IndexOf("=")
            if ($separator -gt 0) { $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
        }
    }
    catch [IO.IOException] { return [ordered]@{} }
    catch [UnauthorizedAccessException] { return [ordered]@{} }
    return $values
}

function Resolve-BridgeRoot([string] $explicit) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) {
        $resolved = (Resolve-Path -LiteralPath $explicit -ErrorAction Stop).Path
        if (-not (Test-Path -LiteralPath (Join-Path $resolved "BRIDGE_MANIFEST.txt") -PathType Leaf)) {
            throw "bridge root does not contain BRIDGE_MANIFEST.txt"
        }
        return $resolved
    }
    $configured = $env:RIMWORLD_DEVBRIDGE_BRIDGE_ROOT
    if (-not [string]::IsNullOrWhiteSpace($configured)) { return Resolve-BridgeRoot $configured }
    return $null
}

function Resolve-UserRoot([string] $explicit) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return (Resolve-Path -LiteralPath $explicit -ErrorAction Stop).Path }
    if (-not [string]::IsNullOrWhiteSpace($env:RIMWORLD_DEVBRIDGE_USER_ROOT)) {
        return (Resolve-Path -LiteralPath $env:RIMWORLD_DEVBRIDGE_USER_ROOT -ErrorAction Stop).Path
    }
    if (-not [string]::IsNullOrWhiteSpace($env:RIMWORLD_USER_DATA)) {
        return (Resolve-Path -LiteralPath $env:RIMWORLD_USER_DATA -ErrorAction Stop).Path
    }
    $candidate = Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
    if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    return $null
}

function Read-BridgeManifest([string] $root) {
    $values = Read-KeyFile (Join-Path $root "BRIDGE_MANIFEST.txt")
    if (-not $values.Contains("bridge") -or -not $values.Contains("protocol") -or -not $values.Contains("schema")) {
        throw "bridge manifest is incomplete"
    }
    return $values
}

function Get-BridgeStatus([string] $bridgeRoot, [string] $userRoot) {
    $result = [ordered]@{ available = $false; reason = "status_unavailable" }
    $root = Resolve-UserRoot $userRoot
    if ($null -eq $root) { $result.reason = "user_data_root_unavailable"; return $result }
    $statusPath = Join-Path $root "RimWorld-DevBridge-Status.txt"
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { return $result }
    $statusFile = Get-Item -LiteralPath $statusPath
    if ([DateTime]::UtcNow - $statusFile.LastWriteTimeUtc -gt [TimeSpan]::FromMinutes(5)) {
        $result.reason = "stale_status"; return $result
    }
    $status = Read-KeyFile $statusPath
    foreach ($required in @("bridge", "version", "protocol", "schema", "processId", "bootId", "session", "transportGeneration")) {
        if (-not $status.Contains($required) -or [string]::IsNullOrWhiteSpace($status[$required])) {
            $result.reason = "invalid_status_$required"; return $result
        }
    }
    if ($status["bridge"] -ne "ON") { $result.reason = "bridge_not_active"; return $result }
    if ([int64]$status["transportGeneration"] -le 0) { $result.reason = "invalid_transport_generation"; return $result }
    $process = Get-Process -Id ([int]$status["processId"]) -ErrorAction SilentlyContinue
    if ($null -eq $process) { $result.reason = "process_not_running"; return $result }
    if ([string]::IsNullOrWhiteSpace($status["token"]) -or [string]::IsNullOrWhiteSpace($status["port"])) {
        $result.reason = "transport_credentials_unavailable"; return $result
    }
    if (-not [string]::IsNullOrWhiteSpace($bridgeRoot)) {
        $manifest = Read-BridgeManifest $bridgeRoot
        if ($status["version"] -ne $manifest["bridge"] -or
            ($status["protocol"] -replace "^v", "") -ne ($manifest["protocol"] -replace "^v", "") -or
            $status["schema"] -ne $manifest["schema"]) {
            $result.reason = "disk_runtime_mismatch"; return $result
        }
        $corePath = Join-Path $bridgeRoot "1.6\Assemblies\RimWorldDevBridge.dll"
        if ((Test-Path -LiteralPath $corePath -PathType Leaf) -and $status.Contains("coreFingerprint")) {
            $fingerprint = (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash
            $moduleId = [Reflection.Assembly]::ReflectionOnlyLoadFrom($corePath).ManifestModule.ModuleVersionId.ToString("N")
            $reported = "$($status["coreFingerprint"])".ToUpperInvariant()
            if ($fingerprint.ToUpperInvariant() -ne $reported -and $moduleId.ToUpperInvariant() -ne $reported) {
                $result.reason = "core_fingerprint_mismatch"; return $result
            }
        }
    }
    $result.available = $true
    $result.reason = "available"
    foreach ($key in $status.Keys) {
        if ($key -ne "token") { $result[$key] = $status[$key] }
    }
    $result.statusPath = $statusPath
    $result.bridgeRoot = $bridgeRoot
    return $result
}

function Invoke-Bridge([string] $command, [string] $argument, [string[]] $values) {
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root")
    $userRoot = Get-Value $values "--user-root"
    $status = Get-BridgeStatus $bridgeRoot $userRoot
    if (-not $status.available) {
        Write-JsonResult $status
        exit 4
    }
    $statusPath = $status.statusPath
    $raw = Read-KeyFile $statusPath
    $agentId = Get-Value $values "--agent-id"
    if ([string]::IsNullOrWhiteSpace($agentId)) { $agentId = [Guid]::NewGuid().ToString("N") }
    $workspaceId = Get-Value $values "--workspace-id" "default"
    $timeout = [int](Get-Value $values "--timeout-ms" "5000")
    if ($timeout -lt 50 -or $timeout -gt 120000) { throw "--timeout-ms must be between 50 and 120000" }
    $options = @("format=json", "agentId=$agentId", "workspaceId=$workspaceId", "timeoutMs=$timeout")
    foreach ($option in $values) {
        if ($option -like "--option=*") { $options += $option.Substring(9) }
    }
    $requestId = [Guid]::NewGuid().ToString("N")
    if ($null -eq $argument) { $argument = "" }
    $line = $raw["token"] + "|" + $requestId + "|" + $command.ToUpperInvariant() + "|" +
        $argument + "|" + ($options -join "&")
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $client.ReceiveTimeout = $timeout
        $client.SendTimeout = $timeout
        $client.Connect($raw["host"], [int]$raw["port"])
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine($line)
        $response = $reader.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($response)) { throw "empty bridge response" }
        $response
    }
    catch {
        Write-JsonResult ([ordered]@{ available = $false; reason = "request_failed"; detail = $_.Exception.Message })
        exit 4
    }
    finally { $client.Dispose() }
}

function Resolve-CoordinatorRoot([string] $userRoot) {
    $resolved = Resolve-UserRoot $userRoot
    if ($null -eq $resolved) { throw "user data root unavailable" }
    $root = Join-Path $resolved "RimWorld-DevBridge-Coordinator"
    [IO.Directory]::CreateDirectory($root) | Out-Null
    return $root
}

function Resolve-CoordinatorExe([string] $bridgeRoot) {
    $candidates = @(
        (Join-Path $bridgeRoot "RestartCoordinator\RimWorldDevBridge.RestartCoordinator.exe"),
        (Join-Path $bridgeRoot "1.6\Assemblies\RestartCoordinator\net472\RimWorldDevBridge.RestartCoordinator.exe"),
        (Join-Path $bridgeRoot "DevTools\RestartCoordinator\bin\Release\net472\RimWorldDevBridge.RestartCoordinator.exe"),
        (Join-Path $PSScriptRoot "RestartCoordinator\bin\Release\net472\RimWorldDevBridge.RestartCoordinator.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "restart coordinator executable not found; build DevTools/RestartCoordinator first"
}

function Invoke-Coordinator([string] $operation, [string[]] $values) {
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root")
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root")
    if ($null -eq $userRoot) { throw "user data root unavailable" }
    $coordinatorRoot = Resolve-CoordinatorRoot $userRoot
    $exe = Resolve-CoordinatorExe $bridgeRoot
    $serveArguments = @("serve", "--root", $coordinatorRoot, "--user-root", $userRoot,
        "--bridge-root", $bridgeRoot)
    $statePath = Join-Path $coordinatorRoot "state.json"
    $serverReady = $false
    try {
        if (Test-Path -LiteralPath $statePath -PathType Leaf) {
            $probe = & $exe status --root $coordinatorRoot --user-root $userRoot --bridge-root $bridgeRoot `
                --ticket "__probe__" 2>$null | Out-String
            if ($LASTEXITCODE -ne 2 -and -not [string]::IsNullOrWhiteSpace($probe)) { $serverReady = $true }
        }
    } catch { $serverReady = $false }
    if (-not $serverReady) {
        $serveArgumentText = ($serveArguments | ForEach-Object {
            if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\\"') + '"' } else { $_ }
        }) -join " "
        Start-Process -FilePath $exe -ArgumentList $serveArgumentText -WindowStyle Hidden | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
            try {
                $probe = & $exe status --root $coordinatorRoot --user-root $userRoot --bridge-root $bridgeRoot `
                    --ticket "__probe__" 2>$null | Out-String
                if ($LASTEXITCODE -eq 2 -and -not [string]::IsNullOrWhiteSpace($probe)) { $serverReady = $true; break }
            } catch { }
        }
    }
    if (-not $serverReady) { throw "restart coordinator did not become ready" }
    $arguments = @($operation, "--root", $coordinatorRoot, "--user-root", $userRoot, "--bridge-root", $bridgeRoot)
    for ($index = 0; $index -lt $values.Count; $index++) {
        $value = $values[$index]
        if ($value -eq "--bridge-root" -or $value -eq "--user-root") { $index++; continue }
        if ($value -like "--*" -and -not $value.Contains("=")) {
            $arguments += $value
            if ($index + 1 -lt $values.Count -and -not $values[$index + 1].StartsWith("--")) {
                $arguments += $values[$index + 1]
                $index++
            }
        } elseif ($value -like "--*=*") { $arguments += $value }
    }
    $output = & $exe @arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ([string]::IsNullOrWhiteSpace($output)) { throw "restart coordinator returned no response" }
    try {
        $response = $output.Trim() | ConvertFrom-Json
        $result = [ordered]@{
            ok = [bool]$response.Ok
            ticket = $response.Ticket
            cycleId = $response.CycleId
            phase = $response.Phase
            error = $response.Error
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$response.Json)) {
            try { $result.details = $response.Json | ConvertFrom-Json } catch { $result.details = $response.Json }
        }
        if ($operation -eq "wait" -and $response.Ok -and $response.Phase -eq "READY") {
            $packageId = Get-Value $values "--package-id" "Lan.RimWorldDevBridge"
            $contextArguments = @("context", "--bridge-root", $bridgeRoot, "--user-root", $userRoot,
                "--package-id", $packageId, "--agent-id", (Get-Value $values "--agent-id"),
                "--workspace-id", (Get-Value $values "--workspace-id" "default"),
                "--timeout-ms", (Get-Value $values "--timeout-ms" "5000"), "--json")
            $contextOutput = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @contextArguments 2>&1 | Out-String).Trim()
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($contextOutput)) {
                try { $result.context = $contextOutput | ConvertFrom-Json } catch { $result.context = $contextOutput }
            } else {
                $result.context = [ordered]@{ available = $false; reason = "post_restart_context_failed" }
            }
        }
        Write-JsonResult $result
    } catch {
        Write-JsonResult ([ordered]@{ ok = $false; error = "invalid_coordinator_response"; detail = $output.Trim() })
        $exitCode = 4
    }
    if ($exitCode -ne 0) { exit $exitCode }
}

$args = @($CommandArguments)
$bridgeRoot = Resolve-BridgeRoot (Get-Value $args "--bridge-root")
$userRoot = Get-Value $args "--user-root"
switch ($Operation.ToLowerInvariant()) {
    "discover" {
        Write-JsonResult (Get-BridgeStatus $bridgeRoot $userRoot)
    }
    "context" {
        $packageId = Get-Value $args "--package-id"
        if ([string]::IsNullOrWhiteSpace($packageId)) { throw "--package-id is required" }
        Invoke-Bridge "AGENT_CONTEXT" ("packageId=" + $packageId) $args
    }
    "describe" {
        $packageId = Get-Value $args "--package-id"
        if ([string]::IsNullOrWhiteSpace($packageId)) { throw "--package-id is required" }
        Invoke-Bridge "AGENT_CONTEXT" ("packageId=" + $packageId) $args
    }
    "call" {
        if ($args.Count -lt 1) { throw "call requires a command" }
        $argument = Get-Value $args "--argument" ""
        Invoke-Bridge $args[0] $argument $args
    }
    "repo" {
        if ($args.Count -lt 1 -or $args[0] -ne "context") { throw "supported repo operation: context" }
        $repoRoot = Get-Value $args "--repo-root"
        if ([string]::IsNullOrWhiteSpace($repoRoot)) { throw "--repo-root is required" }
        & (Join-Path $PSScriptRoot "Test-DevBridgeAgentDescriptor.ps1") -RepositoryRoot $repoRoot | Out-Null
        $descriptorPath = Join-Path $repoRoot "DevTools\DevBridge\agent.json"
        $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
        Invoke-Bridge "AGENT_CONTEXT" ("packageId=" + $descriptor.packageId) $args
    }
    "adapter" {
        if ($args.Count -lt 1) { throw "supported adapter operations: publish, reload" }
        if ($args[0] -eq "reload") { Invoke-Bridge "RELOAD_ADAPTERS" "" $args; break }
        if ($args[0] -eq "publish") {
            $repoRoot = Get-Value $args "--repo-root"
            if ([string]::IsNullOrWhiteSpace($repoRoot)) { throw "--repo-root is required" }
            & (Join-Path $PSScriptRoot "Test-DevBridgeAgentDescriptor.ps1") -RepositoryRoot $repoRoot | Out-Null
            $descriptorPath = Join-Path $repoRoot "DevTools\DevBridge\agent.json"
            $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
            $buildPath = Join-Path $repoRoot $descriptor.buildEntrypoint
            if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) { throw "build entrypoint not found" }
            & $buildPath *>&1 | Out-Null
            Write-JsonResult ([ordered]@{ published = $true; packageId = $descriptor.packageId })
            break
        }
        throw "unsupported adapter operation"
    }
    "restart" {
        if ($args.Count -lt 1 -or @("request", "status", "wait", "register", "launch") -notcontains $args[0]) {
            throw "supported restart operations: request, status, wait, register, launch"
        }
        $operation = $args[0]
        if ($operation -eq "request" -and [string]::IsNullOrWhiteSpace((Get-Value $args "--agent-id"))) {
            throw "--agent-id is required for restart request"
        }
        if (($operation -eq "status" -or $operation -eq "wait") -and
            [string]::IsNullOrWhiteSpace((Get-Value $args "--ticket"))) {
            throw "--ticket is required for restart $operation"
        }
        Invoke-Coordinator $operation $args
    }
    default { throw "unsupported operation: $Operation" }
}
