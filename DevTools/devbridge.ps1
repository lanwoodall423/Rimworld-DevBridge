param(
    [Parameter(Position = 0)] [string] $Operation = "help",
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $CommandArguments
)

$ErrorActionPreference = "Stop"
$script:ExitCode = 0
$script:UnsafeDebug = $false

function Get-Value([string[]] $values, [string] $name, [string] $default = $null) {
    if ($null -eq $values) { return $default }
    for ($index = 0; $index -lt $values.Count; $index++) {
        if ($values[$index] -eq $name -and $index + 1 -lt $values.Count) {
            return $values[$index + 1]
        }
        if ($values[$index].StartsWith($name + "=", [StringComparison]::OrdinalIgnoreCase)) {
            return $values[$index].Substring($name.Length + 1)
        }
    }
    return $default
}

function Has-Flag([string[]] $values, [string] $name) {
    return $null -ne $values -and $values -contains $name
}

function Write-JsonResult($value) {
    $value | ConvertTo-Json -Depth 16 -Compress | Write-Output
}

function Write-Diagnostic([string] $message) {
    [Console]::Error.WriteLine("devbridge: " + $message)
}

function New-ErrorResult([string] $reason, [string] $detail = $null) {
    $result = [ordered]@{ available = $false; reason = $reason }
    if (-not [string]::IsNullOrWhiteSpace($detail)) { $result.detail = $detail }
    return $result
}

function Read-KeyFile([string] $path) {
    $values = [ordered]@{}
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $values }
    try {
        foreach ($line in [IO.File]::ReadAllLines($path)) {
            $separator = $line.IndexOf("=")
            if ($separator -gt 0) {
                $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
            }
        }
    }
    catch [IO.IOException] { return [ordered]@{} }
    catch [UnauthorizedAccessException] { return [ordered]@{} }
    return $values
}

function Read-JsonFile([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try {
        return [IO.File]::ReadAllText($path) | ConvertFrom-Json
    }
    catch {
        throw "client configuration is invalid: $path"
    }
}

function Get-ClientConfig {
    $paths = New-Object 'System.Collections.Generic.List[string]'
    if (-not [string]::IsNullOrWhiteSpace($env:RIMWORLD_DEVBRIDGE_CONFIG)) {
        [void]$paths.Add($env:RIMWORLD_DEVBRIDGE_CONFIG)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        [void]$paths.Add((Join-Path $env:APPDATA "RimWorldDevBridge\\config.json"))
    }
    if (-not [string]::IsNullOrWhiteSpace($env:XDG_CONFIG_HOME)) {
        [void]$paths.Add((Join-Path $env:XDG_CONFIG_HOME "RimWorldDevBridge/config.json"))
    }
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { return Read-JsonFile $path }
    }
    return $null
}

function Resolve-Directory([string] $candidate, [string] $kind) {
    if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    try {
        $resolved = (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "$kind is not a directory"
        }
        return $resolved
    }
    catch {
        throw "${kind}_path_invalid: $candidate"
    }
}

function Resolve-BridgeRoot([string] $explicit, $config) {
    $candidate = $explicit
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:RIMWORLD_DEVBRIDGE_BRIDGE_ROOT }
    if ([string]::IsNullOrWhiteSpace($candidate) -and $config) { $candidate = [string]$config.bridgeRoot }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $scriptRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        if (Test-Path -LiteralPath (Join-Path $scriptRoot "BRIDGE_MANIFEST.txt") -PathType Leaf) {
            $candidate = $scriptRoot
        }
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    $resolved = Resolve-Directory $candidate "bridge_root"
    if (-not (Test-Path -LiteralPath (Join-Path $resolved "BRIDGE_MANIFEST.txt") -PathType Leaf)) {
        throw "bridge_root_invalid: BRIDGE_MANIFEST.txt is missing; supply --bridge-root or RIMWORLD_DEVBRIDGE_BRIDGE_ROOT"
    }
    return $resolved
}

function Resolve-UserRoot([string] $explicit, $config) {
    $candidate = $explicit
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:RIMWORLD_DEVBRIDGE_USER_ROOT }
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:RIMWORLD_USER_DATA }
    if ([string]::IsNullOrWhiteSpace($candidate) -and $config) { $candidate = [string]$config.userRoot }
    if ([string]::IsNullOrWhiteSpace($candidate) -and -not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $candidate = Join-Path $env:USERPROFILE "AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    return Resolve-Directory $candidate "user_root"
}

function Read-BridgeManifest([string] $root) {
    $values = Read-KeyFile (Join-Path $root "BRIDGE_MANIFEST.txt")
    foreach ($required in @("bridge", "protocol", "schema")) {
        if (-not $values.Contains($required)) { throw "bridge manifest is incomplete: missing $required" }
    }
    return $values
}

function Get-AgentId([string] $userRoot, [string] $override) {
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if ($override -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{7,127}$') {
            throw "agent_id_invalid: use 8-128 letters, digits, '.', '_' or '-'"
        }
        return [ordered]@{ value = $override; persisted = $false }
    }
    if ([string]::IsNullOrWhiteSpace($userRoot)) {
        return [ordered]@{ value = [Guid]::NewGuid().ToString("N"); persisted = $false }
    }
    $path = Join-Path $userRoot "RimWorld-DevBridge-AgentId.txt"
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $existing = ([IO.File]::ReadAllText($path)).Trim()
        if ($existing -match '^[A-Za-z0-9][A-Za-z0-9._-]{7,127}$') {
            return [ordered]@{ value = $existing; persisted = $true }
        }
    }
    $value = [Guid]::NewGuid().ToString("N")
    $temporary = $path + ".tmp." + [Guid]::NewGuid().ToString("N")
    try {
        [IO.File]::WriteAllText($temporary, $value + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
        else { Move-Item -LiteralPath $temporary -Destination $path -Force }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $value = ([IO.File]::ReadAllText($path)).Trim()
    }
    return [ordered]@{ value = $value; persisted = $true }
}

function Get-ClientStatePath([string] $userRoot) {
    if ([string]::IsNullOrWhiteSpace($userRoot)) { return $null }
    return Join-Path $userRoot "RimWorld-DevBridge-ClientState.json"
}

function Read-ClientState([string] $userRoot) {
    $path = Get-ClientStatePath $userRoot
    if ($null -eq $path) { return $null }
    try { return Read-JsonFile $path } catch { return $null }
}

function Save-ClientState([string] $userRoot, $state) {
    $path = Get-ClientStatePath $userRoot
    if ($null -eq $path) { return }
    $temporary = $path + ".tmp." + [Guid]::NewGuid().ToString("N")
    try {
        [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-LeaseToken([string] $userRoot, [string] $explicit, [string] $session, [string] $agentId) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return $explicit }
    $state = Read-ClientState $userRoot
    if ($state -and $state.agentId -eq $agentId -and $state.session -eq $session) {
        return [string]$state.leaseToken
    }
    return $null
}

function Save-LeaseFromResponse([string] $userRoot, $response, [string] $session, [string] $agentId) {
    if ($null -eq $response) { return }
    $token = $null
    foreach ($name in @("lease", "leaseToken")) {
        $property = $response.PSObject.Properties[$name]
        if ($property -and $property.Value -is [string] -and -not [string]::IsNullOrWhiteSpace($property.Value)) {
            $token = [string]$property.Value
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($token)) { return }
    Save-ClientState $userRoot ([ordered]@{
        agentId = $agentId
        session = $session
        leaseToken = $token
        savedUtc = [DateTime]::UtcNow.ToString("o")
    })
}

function Clear-LeaseState([string] $userRoot) {
    $path = Get-ClientStatePath $userRoot
    if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

function Get-BridgeStatus([string] $bridgeRoot, [string] $userRoot, [string] $agentId, $config) {
    $result = [ordered]@{ available = $false; reason = "status_unavailable"; agentId = $agentId }
    $root = Resolve-UserRoot $userRoot $config
    if ($null -eq $root) { $result.reason = "user_data_root_unavailable"; return $result }
    $statusPath = Join-Path $root "RimWorld-DevBridge-Status.txt"
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { return $result }
    try { $statusFile = Get-Item -LiteralPath $statusPath } catch { return $result }
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

function Redact-Text([string] $text) {
    if ($script:UnsafeDebug) { return $text }
    return $text -replace '(?i)(token|lease|secret|password)([=:])[^\s|,&}]+', '$1$2[REDACTED]'
}

function Get-ResponseSecret($response) {
    if ($null -eq $response) { return $null }
    foreach ($name in @("lease", "leaseToken")) {
        $property = $response.PSObject.Properties[$name]
        if ($property -and $property.Value -is [string] -and -not [string]::IsNullOrWhiteSpace($property.Value)) {
            return [string]$property.Value
        }
    }
    return $null
}

function Redact-Object($value) {
    if ($script:UnsafeDebug -or $null -eq $value) { return $value }
    if ($value -is [System.Management.Automation.PSCustomObject]) {
        $copy = [ordered]@{}
        foreach ($property in $value.PSObject.Properties) {
            if ($property.Name -match '(?i)^(token|lease|leaseToken|authToken|secret|password)$') {
                $copy[$property.Name] = "[REDACTED]"
            }
            else { $copy[$property.Name] = Redact-Object $property.Value }
        }
        return [pscustomobject]$copy
    }
    if ($value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in $value.Keys) {
            if ([string]$key -match '(?i)^(token|lease|leaseToken|authToken|secret|password)$') {
                $copy[$key] = "[REDACTED]"
            }
            else { $copy[$key] = Redact-Object $value[$key] }
        }
        return $copy
    }
    if ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
        $items = @()
        foreach ($item in $value) { $items += Redact-Object $item }
        return $items
    }
    return $value
}

function Convert-BridgeResponse([string] $raw, [string] $agentId, [string] $idempotencyKey) {
    $response = $null
    try { $response = $raw.Trim() | ConvertFrom-Json } catch { }
    if ($null -eq $response) {
        $response = [pscustomobject]@{ raw = Redact-Text $raw }
    }
    $response = Redact-Object $response
    if ($response -is [System.Management.Automation.PSCustomObject]) {
        Add-Member -InputObject $response -NotePropertyName agentId -NotePropertyValue $agentId -Force
        if (-not [string]::IsNullOrWhiteSpace($idempotencyKey)) {
            Add-Member -InputObject $response -NotePropertyName idempotencyKey -NotePropertyValue $idempotencyKey -Force
        }
    }
    return $response
}

function Test-ResponseOk($response) {
    return $null -ne $response -and $response.PSObject.Properties["status"] -and
        ([string]$response.status).ToUpperInvariant() -eq "OK"
}

function Invoke-BridgeCommand([string] $command, [string] $argument, [string[]] $values,
    [bool] $mutation = $false, [string] $idempotencyKey = $null, [string] $leaseToken = $null) {
    $config = Get-ClientConfig
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
    if ($null -eq $bridgeRoot -or $null -eq $userRoot) {
        $script:ExitCode = 3
        return New-ErrorResult "path_unavailable" "Supply --bridge-root/--user-root or RIMWORLD_DEVBRIDGE_BRIDGE_ROOT/RIMWORLD_DEVBRIDGE_USER_ROOT."
    }
    $status = Get-BridgeStatus $bridgeRoot $userRoot $agent.value $config
    if (-not $status.available -and $status.reason -eq "bridge_not_active") {
        $wake = Invoke-Wake $values
        if ($wake.available) { $status = $wake }
    }
    if (-not $status.available) { $script:ExitCode = 4; return $status }
    $rawStatus = Read-KeyFile $status.statusPath
    if ([string]::IsNullOrWhiteSpace($rawStatus["token"])) {
        $script:ExitCode = 4; return New-ErrorResult "transport_credentials_unavailable"
    }
    $workspaceId = Get-Value $values "--workspace-id" "default"
    $timeoutText = Get-Value $values "--timeout-ms" "5000"
    $timeout = 0
    if (-not [int]::TryParse($timeoutText, [ref]$timeout) -or $timeout -lt 50 -or $timeout -gt 120000) {
        throw "timeout_ms_invalid: use a value between 50 and 120000"
    }
    if ($mutation -and [string]::IsNullOrWhiteSpace($idempotencyKey)) {
        $idempotencyKey = [Guid]::NewGuid().ToString("N")
    }
    $effectiveLease = $leaseToken
    if ([string]::IsNullOrWhiteSpace($effectiveLease)) {
        $effectiveLease = Get-LeaseToken $userRoot $null $status.session $agent.value
    }
    $options = @("format=json", "agentId=$($agent.value)", "workspaceId=$workspaceId", "timeoutMs=$timeout")
    if (-not [string]::IsNullOrWhiteSpace($effectiveLease)) { $options += "lease=$effectiveLease" }
    if (-not [string]::IsNullOrWhiteSpace($idempotencyKey)) { $options += "idempotency=$idempotencyKey" }
    if (Has-Flag $values "--allow-expensive") { $options += "allowExpensive=true" }
    foreach ($option in $values) {
        if ($option -like "--option=*") { $options += $option.Substring(9) }
    }
    if ($null -eq $argument) { $argument = "" }
    $requestId = [Guid]::NewGuid().ToString("N")
    $line = $rawStatus["token"] + "|" + $requestId + "|" + $command.ToUpperInvariant() + "|" +
        $argument + "|" + ($options -join "&")
    $client = [Net.Sockets.TcpClient]::new()
    $connect = $null
    $writer = $null
    $reader = $null
    $stream = $null
    try {
        $client.ReceiveTimeout = $timeout
        $client.SendTimeout = $timeout
        $connect = $client.BeginConnect($rawStatus["host"], [int]$rawStatus["port"], $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($timeout)) {
            $script:ExitCode = 4
            return New-ErrorResult "connection_timeout"
        }
        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer.AutoFlush = $true
        $writer.WriteLine($line)
        $rawResponse = $reader.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($rawResponse)) { throw "empty bridge response" }
        $parsed = $null
        try { $parsed = $rawResponse.Trim() | ConvertFrom-Json } catch { }
        if ($command -eq "WRITE_LEASE" -and $parsed -and (Test-ResponseOk $parsed)) {
            Save-LeaseFromResponse $userRoot $parsed $status.session $agent.value
        }
        if ($command -eq "REVOKE_WRITE_LEASE" -and $parsed -and (Test-ResponseOk $parsed)) {
            Clear-LeaseState $userRoot
        }
        $response = Convert-BridgeResponse $rawResponse $agent.value $idempotencyKey
        if (-not (Test-ResponseOk $response) -and $response.PSObject.Properties["status"]) {
            $script:ExitCode = 6
        }
        return $response
    }
    catch {
        $script:ExitCode = 4
        return New-ErrorResult "request_failed" (Redact-Text $_.Exception.Message)
    }
    finally {
        if ($connect -and $connect.AsyncWaitHandle) { $connect.AsyncWaitHandle.Dispose() }
        if ($writer) { $writer.Dispose() }
        if ($reader) { $reader.Dispose() }
        if ($stream) { $stream.Dispose() }
        $client.Dispose()
    }
}

function Invoke-Help {
    return [ordered]@{
        client = "devbridge"
        commands = @(
            "discover", "wake", "read --command <name>", "call <command>",
            "lease acquire|inspect|renew|release", "mutate --command <name>", "cancel --request-id <id>",
            "context --package-id <id>", "describe --package-id <id>", "repo context", "adapter publish|reload",
            "restart request|status|wait|register|launch", "validate", "help"
        )
        exitCodes = [ordered]@{ success = 0; invalidArguments = 2; pathOrUnavailable = 3; transport = 4; stale = 5; bridgeRejected = 6 }
        secrets = "transport and lease tokens are redacted; use --unsafe-debug only for explicit local debugging"
    }
}

function Invoke-Validate([string[]] $values) {
    $config = Get-ClientConfig
    $root = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    if ($null -eq $root) { throw "bridge_root_required: supply --bridge-root or RIMWORLD_DEVBRIDGE_BRIDGE_ROOT" }
    $required = @(
        "About/About.xml", "LoadFolders.xml", "BRIDGE_MANIFEST.txt", "BRIDGE_HANDOFF.md", "LICENSE",
        "AGENTS.md", "1.6/Assemblies/RimWorldDevBridge.dll", "RestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe",
        "DevTools/devbridge.ps1", "DevTools/Send-RimWorldBridge.ps1", "DevTools/DEVBRIDGE_AGENT.md"
    )
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root ($_ -replace '/', '\')) -PathType Leaf) })
    if ($missing.Count -gt 0) { throw "package_required_file_missing: $($missing -join ',')" }
    $manifest = Read-BridgeManifest $root
    if ($manifest.license -ne 'MIT' -or $manifest.licenseFile -ne 'LICENSE') {
        throw 'package_license_metadata_invalid: expected MIT and LICENSE'
    }
    return [ordered]@{ valid = $true; bridgeRoot = $root; bridge = $manifest.bridge; protocol = $manifest.protocol; schema = $manifest.schema; requiredFiles = $required.Count }
}

function Resolve-GameExecutable([string] $explicit, $config) {
    $candidate = $explicit
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:RIMWORLD_DEVBRIDGE_GAME_PATH }
    if ([string]::IsNullOrWhiteSpace($candidate) -and $config) { $candidate = [string]$config.gamePath }
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return (Resolve-Path -LiteralPath $candidate).Path }
        throw "game_path_invalid: $candidate"
    }
    foreach ($name in @("RimWorldWin64.exe", "RimWorldLinux", "RimWorldMac")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf)) { return $command.Source }
    }
    return $null
}

function Invoke-Wake([string[]] $values) {
    $config = Get-ClientConfig
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    if ($null -eq $userRoot) { $script:ExitCode = 3; return New-ErrorResult "user_root_unavailable" "Supply --user-root or RIMWORLD_DEVBRIDGE_USER_ROOT." }
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
    $statusPath = Join-Path $userRoot "RimWorld-DevBridge-Status.txt"
    $status = Read-KeyFile $statusPath
    $started = $false
    if ($status["bridge"] -ne "ON" -and [string]::IsNullOrWhiteSpace($status["processId"])) {
        if (-not (Has-Flag $values "--start")) {
            $script:ExitCode = 3
            return New-ErrorResult "process_not_running" "Use --start with --game-path, RIMWORLD_DEVBRIDGE_GAME_PATH, or configured gamePath."
        }
        $game = Resolve-GameExecutable (Get-Value $values "--game-path") $config
        if ($null -eq $game) {
            $script:ExitCode = 3
            return New-ErrorResult "game_path_required" "Supply --game-path or RIMWORLD_DEVBRIDGE_GAME_PATH; no broad installation scan is performed."
        }
        $working = Split-Path -Parent $game
        $gameArguments = Get-Value $values "--game-arguments" ""
        $parameters = @{ FilePath = $game; WorkingDirectory = $working; PassThru = $true }
        if (-not [string]::IsNullOrWhiteSpace($gameArguments)) { $parameters.ArgumentList = $gameArguments }
        $process = Start-Process @parameters
        $started = $true
        $status = [ordered]@{ processId = $process.Id }
    }
    if ($status["bridge"] -ne "ON") {
        [IO.File]::WriteAllText((Join-Path $userRoot "RimWorld-DevBridge-Wake.request"), "")
    }
    $timeout = 5000
    [int]::TryParse((Get-Value $values "--timeout-ms" "5000"), [ref]$timeout) | Out-Null
    if ($timeout -lt 50 -or $timeout -gt 120000) { throw "timeout_ms_invalid: use a value between 50 and 120000" }
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeout)
    do {
        Start-Sleep -Milliseconds 50
        $status = Read-KeyFile $statusPath
    } while ($status["bridge"] -ne "ON" -and [DateTime]::UtcNow -lt $deadline)
    if ($status["bridge"] -ne "ON") {
        $script:ExitCode = 4
        return New-ErrorResult "bridge_did_not_wake"
    }
    $result = Get-BridgeStatus $bridgeRoot $userRoot $agent.value $config
    $result.woke = $true
    $result.started = $started
    return $result
}

function Invoke-Coordinator([string] $operation, [string[]] $values) {
    $config = Get-ClientConfig
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    if ($null -eq $bridgeRoot -or $null -eq $userRoot) { $script:ExitCode = 3; return New-ErrorResult "path_unavailable" }
    $coordinatorRoot = Join-Path $userRoot "RimWorld-DevBridge-Coordinator"
    [IO.Directory]::CreateDirectory($coordinatorRoot) | Out-Null
    $candidates = @(
        (Join-Path $bridgeRoot "RestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe"),
        (Join-Path $bridgeRoot "1.6/Assemblies/RestartCoordinator/net472/RimWorldDevBridge.RestartCoordinator.exe"),
        (Join-Path $bridgeRoot "DevTools/RestartCoordinator/bin/Release/net472/RimWorldDevBridge.RestartCoordinator.exe"),
        (Join-Path $PSScriptRoot "RestartCoordinator/bin/Release/net472/RimWorldDevBridge.RestartCoordinator.exe")
    )
    $exe = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($null -eq $exe) { $script:ExitCode = 3; return New-ErrorResult "restart_coordinator_unavailable" "Build or package the coordinator executable." }
    $commonArguments = @('--root', $coordinatorRoot, '--user-root', $userRoot, '--bridge-root', $bridgeRoot)
    $probeOutput = & $exe heartbeat @commonArguments --timeout-ms 250 2>&1 | Out-String
    $pipeReady = $LASTEXITCODE -eq 0
    if (-not $pipeReady) {
        $serverArguments = @('--serve', '--root', $coordinatorRoot, '--user-root', $userRoot, '--bridge-root', $bridgeRoot)
        $quoted = @($serverArguments | ForEach-Object {
            $text = [string]$_
            if ($text -match '[\s"]') { '"' + $text.Replace('"', '\"') + '"' } else { $text }
        })
        Start-Process -FilePath $exe -ArgumentList ($quoted -join ' ') -WindowStyle Hidden | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 50
            $probeOutput = & $exe heartbeat @commonArguments --timeout-ms 250 2>&1 | Out-String
            $pipeReady = $LASTEXITCODE -eq 0
        } while (-not $pipeReady -and [DateTime]::UtcNow -lt $deadline)
    }
    if (-not $pipeReady) { $script:ExitCode = 4; return New-ErrorResult "restart_coordinator_unavailable" }
    $base = @($operation, "--root", $coordinatorRoot, "--user-root", $userRoot, "--bridge-root", $bridgeRoot)
    for ($index = 0; $index -lt $values.Count; $index++) {
        $value = $values[$index]
        if ($value -eq "--bridge-root" -or $value -eq "--user-root") { $index++; continue }
        if ($value -like "--*" -and -not $value.Contains("=")) {
            $base += $value
            if ($index + 1 -lt $values.Count -and -not $values[$index + 1].StartsWith("--")) { $base += $values[$index + 1]; $index++ }
        }
        elseif ($value -like "--*=*") { $base += $value }
    }
    $output = & $exe @base 2>&1 | Out-String
    $exit = $LASTEXITCODE
    try { $response = $output.Trim() | ConvertFrom-Json } catch { $response = [ordered]@{ ok = $false; error = "invalid_coordinator_response"; detail = Redact-Text $output.Trim() }; $exit = 4 }
    $result = [ordered]@{
        ok = [bool]$response.Ok
        ticket = $response.Ticket
        cycleId = $response.CycleId
        phase = $response.Phase
        error = $response.Error
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$response.Json)) {
        try { $result.details = $response.Json | ConvertFrom-Json } catch { $result.details = Redact-Text $response.Json }
    }
    if ($operation -eq "wait" -and $result.ok -and $result.phase -eq "READY") {
        $packageId = Get-Value $values "--package-id" "Lan.RimWorldDevBridge"
        $context = Invoke-BridgeCommand "AGENT_CONTEXT" ("packageId=" + $packageId) $values $false $null $null
        $result.context = $context
        if (-not (Test-ResponseOk $context)) { $result.contextHandshake = "failed"; $script:ExitCode = 5 }
    }
    if ($exit -ne 0) { $script:ExitCode = $exit }
    return $result
}

function Get-CallCommand([string[]] $values) {
    $explicit = Get-Value $values "--command"
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return $explicit }
    foreach ($value in $values) { if (-not $value.StartsWith("--")) { return $value } }
    return $null
}

try {
    $args = @($CommandArguments)
    $script:UnsafeDebug = Has-Flag $args "--unsafe-debug"
    if ($script:UnsafeDebug) { Write-Diagnostic "UNSAFE DEBUG ENABLED: transport and lease secrets may be printed" }
    $config = Get-ClientConfig
    $operationName = $Operation.ToLowerInvariant()
    $result = $null
    switch ($operationName) {
        "help" { $result = Invoke-Help }
        "validate" { $result = Invoke-Validate $args }
        "discover" {
            $userRoot = Resolve-UserRoot (Get-Value $args "--user-root") $config
            $bridgeRoot = Resolve-BridgeRoot (Get-Value $args "--bridge-root") $config
            $agent = Get-AgentId $userRoot (Get-Value $args "--agent-id")
            $result = Get-BridgeStatus $bridgeRoot $userRoot $agent.value $config
            $result.agentId = $agent.value
            $result.agentIdPersisted = $agent.persisted
            if (-not $result.available) { $script:ExitCode = 4 }
        }
        "wake" { $result = Invoke-Wake $args }
        "context" {
            $package = Get-Value $args "--package-id"
            if ([string]::IsNullOrWhiteSpace($package)) { throw "package_id_required: use --package-id" }
            $result = Invoke-BridgeCommand "AGENT_CONTEXT" ("packageId=" + $package) $args
        }
        "describe" {
            $package = Get-Value $args "--package-id"
            if ([string]::IsNullOrWhiteSpace($package)) { throw "package_id_required: use --package-id" }
            $result = Invoke-BridgeCommand "AGENT_CONTEXT" ("packageId=" + $package) $args
        }
        "read" {
            $command = Get-CallCommand $args
            if ([string]::IsNullOrWhiteSpace($command)) { throw "command_required: use read --command <name>" }
            $result = Invoke-BridgeCommand $command (Get-Value $args "--argument" "") $args
        }
        "call" {
            $command = Get-CallCommand $args
            if ([string]::IsNullOrWhiteSpace($command)) { throw "command_required: use call <command> or --command <name>" }
            $result = Invoke-BridgeCommand $command (Get-Value $args "--argument" "") $args $false (Get-Value $args "--idempotency-key") (Get-Value $args "--lease-token")
        }
        "mutate" {
            $command = Get-CallCommand $args
            if ([string]::IsNullOrWhiteSpace($command)) { throw "command_required: use mutate --command <name>" }
            $result = Invoke-BridgeCommand $command (Get-Value $args "--argument" "") $args $true (Get-Value $args "--idempotency-key") (Get-Value $args "--lease-token")
        }
        "cancel" {
            $requestId = Get-Value $args "--request-id" (Get-Value $args "--argument")
            if ([string]::IsNullOrWhiteSpace($requestId)) { throw "request_id_required: use --request-id" }
            $result = Invoke-BridgeCommand "CANCEL" $requestId $args
        }
        "lease" {
            if ($args.Count -lt 1) { throw "lease_operation_required: use acquire, inspect, renew, or release" }
            switch ($args[0].ToLowerInvariant()) {
                "acquire" { $result = Invoke-BridgeCommand "WRITE_LEASE" (Get-Value $args "--context" "sandbox") $args }
                "inspect" { $result = Invoke-BridgeCommand "STATUS" "" $args }
                "renew" { $result = Invoke-BridgeCommand "RENEW_WRITE_LEASE" "" $args $false $null (Get-Value $args "--lease-token") }
                "release" { $result = Invoke-BridgeCommand "REVOKE_WRITE_LEASE" "" $args $false $null (Get-Value $args "--lease-token") }
                default { throw "lease_operation_invalid: use acquire, inspect, renew, or release" }
            }
        }
        "repo" {
            if ($args.Count -lt 1 -or $args[0] -ne "context") { throw "repo_operation_invalid: supported operation is context" }
            $repoRoot = Get-Value $args "--repo-root"
            if ([string]::IsNullOrWhiteSpace($repoRoot)) { throw "repo_root_required: use --repo-root" }
            & (Join-Path $PSScriptRoot "Test-DevBridgeAgentDescriptor.ps1") -RepositoryRoot $repoRoot | Out-Null
            $descriptor = Get-Content -LiteralPath (Join-Path $repoRoot "DevTools\DevBridge\agent.json") -Raw | ConvertFrom-Json
            $result = Invoke-BridgeCommand "AGENT_CONTEXT" ("packageId=" + $descriptor.packageId) $args
        }
        "adapter" {
            if ($args.Count -lt 1) { throw "adapter_operation_required: use publish or reload" }
            if ($args[0] -eq "reload") { $result = Invoke-BridgeCommand "RELOAD_ADAPTERS" "" $args; break }
            if ($args[0] -eq "publish") {
                $repoRoot = Get-Value $args "--repo-root"
                if ([string]::IsNullOrWhiteSpace($repoRoot)) { throw "repo_root_required: use --repo-root" }
                & (Join-Path $PSScriptRoot "Test-DevBridgeAgentDescriptor.ps1") -RepositoryRoot $repoRoot | Out-Null
                $descriptor = Get-Content -LiteralPath (Join-Path $repoRoot "DevTools\DevBridge\agent.json") -Raw | ConvertFrom-Json
                $buildPath = Join-Path $repoRoot $descriptor.buildEntrypoint
                if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) { throw "build_entrypoint_missing: $buildPath" }
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildPath *>&1 | Out-Null
                $result = [ordered]@{ published = $true; packageId = $descriptor.packageId; adapterDirectory = $descriptor.adapterDirectory }
                break
            }
            throw "adapter_operation_invalid: use publish or reload"
        }
        "restart" {
            if ($args.Count -lt 1) { throw "restart_operation_required: use request, status, wait, register, or launch" }
            if ($args[0] -eq "request" -and [string]::IsNullOrWhiteSpace((Get-Value $args "--agent-id"))) { throw "agent_id_required: restart request" }
            if (($args[0] -eq "status" -or $args[0] -eq "wait") -and [string]::IsNullOrWhiteSpace((Get-Value $args "--ticket"))) { throw "ticket_required: restart $($args[0])" }
            $result = Invoke-Coordinator $args[0] $args
        }
        default { throw "operation_invalid: use help to list supported operations" }
    }
    Write-JsonResult $result
    if ($script:ExitCode -ne 0) { exit $script:ExitCode }
}
catch {
    $detail = Redact-Text $_.Exception.Message
    Write-Diagnostic $detail
    Write-JsonResult (New-ErrorResult "client_error" $detail)
    exit 2
}
