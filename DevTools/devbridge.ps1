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
    (Redact-Object $value) | ConvertTo-Json -Depth 16 -Compress | Write-Output
}

function Write-Diagnostic([string] $message) {
    [Console]::Error.WriteLine("devbridge: " + $message)
}

function New-ErrorResult([string] $reason, [string] $detail = $null) {
    $result = [ordered]@{ available = $false; reason = $reason }
    if (-not [string]::IsNullOrWhiteSpace($detail)) { $result.detail = Redact-Text $detail }
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
    $redacted = $text -replace '(?i)(token|lease|secret|password)([=:])[^\s|,&}]+', '$1$2[REDACTED]'
    return $redacted -replace '(?i)\b[A-Z]:\\[^\s|,&}]+', '[REDACTED_PATH]'
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
            elseif ($property.Name -match '(?i)^(path|bridgeRoot|statusPath|coordinatorPath|gamePath|executable|workingDirectory|savePath|userDataRoot|profilePath)$') {
                $copy[$property.Name] = "[REDACTED_PATH]"
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
            elseif ([string]$key -match '(?i)^(path|bridgeRoot|statusPath|coordinatorPath|gamePath|executable|workingDirectory|savePath|userDataRoot|profilePath)$') {
                $copy[$key] = "[REDACTED_PATH]"
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
            "restart authorize-sandbox|revoke-sandbox|request|status|wait|register|launch|ensure",
            "validate --layout auto|source|package", "help"
        )
        exitCodes = [ordered]@{ success = 0; invalidArguments = 2; pathOrUnavailable = 3; transport = 4; stale = 5; bridgeRejected = 6 }
        secrets = "transport and lease tokens are redacted; use --unsafe-debug only for explicit local debugging"
    }
}

function Get-PackageRequiredFiles {
    return @(
        "About/About.xml", "LoadFolders.xml", "BRIDGE_MANIFEST.txt", "BRIDGE_HANDOFF.md", "LICENSE",
        "AGENTS.md", "1.6/Assemblies/RimWorldDevBridge.dll", "RestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe",
        "DevTools/devbridge.ps1", "DevTools/Send-RimWorldBridge.ps1", "DevTools/DEVBRIDGE_AGENT.md"
    )
}

function Get-SourceRequiredFiles {
    return @(
        "BRIDGE_MANIFEST.txt", "Directory.Build.props", "Directory.Build.targets",
        "Source/RimWorldDevBridge/RimWorldDevBridge.csproj",
        "Source/RimWorldDevBridge/BridgeRestartCoordinator.cs",
        "DevTools/RestartCoordinator/RimWorldDevBridge.RestartCoordinator.csproj",
        "DevTools/RestartCoordinator/Program.cs", "DevTools/devbridge.ps1"
    )
}

function Get-BridgeLayout([string] $root, [string] $requested = "auto") {
    $requested = $requested.ToLowerInvariant()
    if ($requested -notin @("auto", "source", "package")) { throw "layout_invalid: use auto, source, or package" }
    $packageFiles = Get-PackageRequiredFiles
    $sourceFiles = Get-SourceRequiredFiles
    $missingPackage = @($packageFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $root ($_ -replace '/', '\')) -PathType Leaf)
    })
    $missingSource = @($sourceFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $root ($_ -replace '/', '\')) -PathType Leaf)
    })
    $layout = $requested
    if ($requested -eq "auto") {
        if ($missingPackage.Count -eq 0) { $layout = "package" }
        elseif ($missingSource.Count -eq 0) { $layout = "source" }
        else { $layout = "unknown" }
    }
    $selectedMissing = if ($layout -eq "package") { $missingPackage } elseif ($layout -eq "source") { $missingSource } else { @() }
    return [ordered]@{
        requested = $requested
        layout = $layout
        packageComplete = $missingPackage.Count -eq 0
        sourceComplete = $missingSource.Count -eq 0
        packageMissing = $missingPackage
        sourceMissing = $missingSource
        selectedMissing = $selectedMissing
    }
}

function Get-CoordinatorIdentity([string] $path) {
    try {
        $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($path)
        return [ordered]@{
            name = $assemblyName.Name
            version = [string]$assemblyName.Version
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            bytes = (Get-Item -LiteralPath $path).Length
        }
    }
    catch { return $null }
}

function Get-CoordinatorInfo([string] $root, $layoutInfo, [string] $explicitPath = $null, [bool] $ensureBuild = $false) {
    $sourceProject = Join-Path $root "DevTools\RestartCoordinator\RimWorldDevBridge.RestartCoordinator.csproj"
    $outputPath = Join-Path $root "1.6\Assemblies\RestartCoordinator\net472\RimWorldDevBridge.RestartCoordinator.exe"
    $configured = $explicitPath
    if ([string]::IsNullOrWhiteSpace($configured)) {
        $config = Get-ClientConfig
        if ($config) { $configured = [string]$config.coordinatorPath }
    }
    $candidatePaths = New-Object 'System.Collections.Generic.List[string]'
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        [void]$candidatePaths.Add($configured)
    }
    else {
        foreach ($candidate in @(
            (Join-Path $root "RestartCoordinator\RimWorldDevBridge.RestartCoordinator.exe"),
            $outputPath,
            (Join-Path $root "DevTools\RestartCoordinator\bin\Release\net472\RimWorldDevBridge.RestartCoordinator.exe"),
            (Join-Path $PSScriptRoot "RestartCoordinator\bin\Release\net472\RimWorldDevBridge.RestartCoordinator.exe")
        )) {
            if (-not $candidatePaths.Contains($candidate)) { [void]$candidatePaths.Add($candidate) }
        }
    }
    $expectedName = "RimWorldDevBridge.RestartCoordinator"
    foreach ($candidate in $candidatePaths) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $identity = Get-CoordinatorIdentity $candidate
        if ($identity -and $identity.name -eq $expectedName) {
            return [ordered]@{
                status = "available"
                available = $true
                path = $candidate
                relativePath = if ($candidate.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                    $candidate.Substring($root.Length).TrimStart('\', '/')
                } else { "configured" }
                identity = $identity
                sourceProject = (Test-Path -LiteralPath $sourceProject -PathType Leaf)
                buildAttempted = $false
            }
        }
    }
    if ($ensureBuild -and $layoutInfo.layout -eq "source" -and (Test-Path -LiteralPath $sourceProject -PathType Leaf)) {
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($null -eq $dotnet) {
            return [ordered]@{ status = "missing_build_tooling"; available = $false; buildAttempted = $false; sourceProject = $true }
        }
        try {
            $buildOutput = & $dotnet.Source build $sourceProject -c Release --nologo "/p:DevBridgeCoordinatorOutputRoot=$(Split-Path -Parent $outputPath)" 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                return [ordered]@{ status = "build_failed"; available = $false; buildAttempted = $true; sourceProject = $true; detail = Redact-Text $buildOutput }
            }
        }
        catch {
            return [ordered]@{ status = "build_failed"; available = $false; buildAttempted = $true; sourceProject = $true; detail = Redact-Text $_.Exception.Message }
        }
        if (Test-Path -LiteralPath $outputPath -PathType Leaf) {
            $identity = Get-CoordinatorIdentity $outputPath
            if ($identity -and $identity.name -eq $expectedName) {
                return [ordered]@{
                    status = "available"
                    available = $true
                    path = $outputPath
                    relativePath = $outputPath.Substring($root.Length).TrimStart('\', '/')
                    identity = $identity
                    sourceProject = $true
                    buildAttempted = $true
                }
            }
        }
        return [ordered]@{ status = "invalid"; available = $false; buildAttempted = $true; sourceProject = $true }
    }
    $status = if ($layoutInfo.layout -eq "source" -and $layoutInfo.sourceComplete) { "buildable" } else { "missing" }
    if (-not [string]::IsNullOrWhiteSpace($configured)) { $status = "invalid" }
    return [ordered]@{ status = $status; available = $false; buildAttempted = $false; sourceProject = Test-Path -LiteralPath $sourceProject -PathType Leaf }
}

function Invoke-Validate([string[]] $values) {
    $config = Get-ClientConfig
    $root = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    if ($null -eq $root) { throw "bridge_root_required: supply --bridge-root or RIMWORLD_DEVBRIDGE_BRIDGE_ROOT" }
    $manifest = Read-BridgeManifest $root
    $layoutInfo = Get-BridgeLayout $root (Get-Value $values "--layout" "auto")
    $coordinator = Get-CoordinatorInfo $root $layoutInfo (Get-Value $values "--coordinator-path") (Has-Flag $values "--ensure-runtime-tools")
    $packageMetadataValid = $manifest.license -eq 'MIT' -and $manifest.licenseFile -eq 'LICENSE'
    $valid = $false
    $reason = $null
    if ($layoutInfo.layout -eq "package") {
        $valid = $layoutInfo.packageComplete -and $packageMetadataValid -and $coordinator.status -eq "available"
        if (-not $layoutInfo.packageComplete) { $reason = "package_required_file_missing" }
        elseif (-not $packageMetadataValid) { $reason = "package_license_metadata_invalid" }
        elseif (-not $coordinator.available) { $reason = "coordinator_invalid" }
    }
    elseif ($layoutInfo.layout -eq "source") {
        $valid = $layoutInfo.sourceComplete -and $coordinator.status -ne "invalid"
        if (-not $layoutInfo.sourceComplete) { $reason = "source_required_file_missing" }
        elseif ($coordinator.status -eq "invalid") { $reason = "coordinator_invalid" }
    }
    else { $reason = "bridge_layout_unrecognized" }
    if (-not $valid) { $script:ExitCode = 2 }
    return [ordered]@{
        valid = $valid
        reason = $reason
        layout = $layoutInfo.layout
        bridge = $manifest.bridge
        protocol = $manifest.protocol
        schema = $manifest.schema
        packageMetadataValid = $packageMetadataValid
        requiredFiles = (Get-PackageRequiredFiles).Count
        packageComplete = $layoutInfo.packageComplete
        sourceComplete = $layoutInfo.sourceComplete
        packageMissing = $layoutInfo.packageMissing
        sourceMissing = $layoutInfo.sourceMissing
        coordinator = $coordinator
        runtimeToolsReady = $coordinator.available
    }
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

function Write-JsonAtomic([string] $path, $value) {
    $temporary = $path + ".tmp." + [Guid]::NewGuid().ToString("N")
    try {
        [IO.File]::WriteAllText($temporary, ($value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Get-ManagedLaunchProfile([string[]] $values, $config, [string] $userRoot) {
    $profilePath = Get-Value $values "--launch-profile-path"
    if ([string]::IsNullOrWhiteSpace($profilePath) -and $config) { $profilePath = [string]$config.managedTestLaunchProfilePath }
    if ([string]::IsNullOrWhiteSpace($profilePath)) { $profilePath = Join-Path $userRoot "RimWorld-DevBridge-ManagedTestLaunch.json" }
    $existing = if (Test-Path -LiteralPath $profilePath -PathType Leaf) { Read-JsonFile $profilePath } else { $null }
    $explicitGame = Get-Value $values "--game-path"
    $executable = if (-not [string]::IsNullOrWhiteSpace($explicitGame)) { Resolve-GameExecutable $explicitGame $config } elseif ($existing) { [string]$existing.executable } else { Resolve-GameExecutable $null $config }
    if ([string]::IsNullOrWhiteSpace($executable) -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "managed_test_executable_invalid: supply --game-path" }
    $working = Get-Value $values "--working-directory"
    if ([string]::IsNullOrWhiteSpace($working) -and $existing) { $working = [string]$existing.workingDirectory }
    if ([string]::IsNullOrWhiteSpace($working)) { $working = Split-Path -Parent $executable }
    $working = Resolve-Directory $working "working_directory"
    $arguments = Get-Value $values "--arguments"
    if ($null -eq $arguments) { $arguments = Get-Value $values "--game-arguments" }
    if ($null -eq $arguments -and $existing) { $arguments = [string]$existing.arguments }
    if ($null -eq $arguments) { $arguments = "" }
    $profileUserRoot = Get-Value $values "--profile-user-root"
    if ([string]::IsNullOrWhiteSpace($profileUserRoot) -and $existing) { $profileUserRoot = [string]$existing.userDataRoot }
    if ([string]::IsNullOrWhiteSpace($profileUserRoot)) { $profileUserRoot = $userRoot }
    $profileUserRoot = Resolve-Directory $profileUserRoot "user_data_root"
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($profileUserRoot, $userRoot)) { throw "managed_test_user_root_mismatch" }
    $modConfiguration = Get-Value $values "--mod-configuration"
    if ([string]::IsNullOrWhiteSpace($modConfiguration) -and $existing) { $modConfiguration = [string]$existing.modConfiguration }
    if ([string]::IsNullOrWhiteSpace($modConfiguration) -and $config) { $modConfiguration = [string]$config.managedTestModConfiguration }
    if ([string]::IsNullOrWhiteSpace($modConfiguration)) { throw "managed_test_mod_configuration_required" }
    $profile = [ordered]@{
        profile = "managed-test"
        executable = [IO.Path]::GetFullPath($executable)
        workingDirectory = $working
        arguments = [string]$arguments
        userDataRoot = $profileUserRoot
        modConfiguration = [string]$modConfiguration
        executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        validatedUtc = [DateTime]::UtcNow.ToString("o")
    }
    Write-JsonAtomic $profilePath $profile
    $profile.profilePath = $profilePath
    return $profile
}

function Get-SandboxAuthorizationPath([string[]] $values, $config, [string] $userRoot) {
    $path = Get-Value $values "--sandbox-authorization-path"
    if ([string]::IsNullOrWhiteSpace($path) -and $config) { $path = [string]$config.sandboxAuthorizationPath }
    if ([string]::IsNullOrWhiteSpace($path)) { $path = Join-Path $userRoot "RimWorld-DevBridge-SandboxAuthorization.json" }
    $fullPath = [IO.Path]::GetFullPath($path)
    $rootPrefix = [IO.Path]::GetFullPath($userRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "sandbox_authorization_path_invalid: authorization must remain under the user root"
    }
    return $fullPath
}

function Test-SandboxAuthorizationProfile($authorization, $profile, [string] $userRoot) {
    if ($null -eq $authorization -or $authorization.schema -ne 1 -or
        $authorization.policy -ne "explicit-operator-disposable-sandbox" -or
        $authorization.scope -ne "coordinator-owned-managed-test" -or
        $authorization.profile -ne "managed-test" -or
        $authorization.operatorConfirmed -ne $true) { return $false }
    $pathMatches = [StringComparer]::OrdinalIgnoreCase
    $stringMatches = [StringComparer]::Ordinal
    return $pathMatches.Equals([IO.Path]::GetFullPath([string]$authorization.userDataRoot), $userRoot) -and
        $pathMatches.Equals([IO.Path]::GetFullPath([string]$authorization.executable), [IO.Path]::GetFullPath([string]$profile.executable)) -and
        $pathMatches.Equals([IO.Path]::GetFullPath([string]$authorization.workingDirectory), [IO.Path]::GetFullPath([string]$profile.workingDirectory)) -and
        $stringMatches.Equals([string]$authorization.arguments, [string]$profile.arguments) -and
        $stringMatches.Equals([string]$authorization.modConfiguration, [string]$profile.modConfiguration) -and
        $stringMatches.Equals([string]$authorization.executableSha256, [string]$profile.executableSha256)
}

function Get-SandboxAuthorization([string[]] $values, $config, [string] $userRoot, $profile) {
    $path = Get-SandboxAuthorizationPath $values $config $userRoot
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [ordered]@{ authorized = $false; reason = "sandbox_authorization_required"; path = $path }
    }
    try { $authorization = Read-JsonFile $path }
    catch { return [ordered]@{ authorized = $false; reason = "sandbox_authorization_invalid"; path = $path } }
    try {
        if (-not (Test-SandboxAuthorizationProfile $authorization $profile $userRoot)) {
            return [ordered]@{ authorized = $false; reason = "sandbox_authorization_scope_mismatch"; path = $path }
        }
    }
    catch { return [ordered]@{ authorized = $false; reason = "sandbox_authorization_invalid"; path = $path } }
    return [ordered]@{
        authorized = $true
        reason = "sandbox_authorized"
        path = $path
        authorizedUtc = [string]$authorization.authorizedUtc
        scope = [string]$authorization.scope
    }
}

function Invoke-SandboxAuthorization([string[]] $values, [bool] $revoke = $false) {
    $config = Get-ClientConfig
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    if ($null -eq $bridgeRoot -or $null -eq $userRoot) {
        $script:ExitCode = 3
        return New-ErrorResult "configuration_error" "bridge and user roots are required"
    }
    try {
        $path = Get-SandboxAuthorizationPath $values $config $userRoot
        if ($revoke) {
            if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
            return [ordered]@{ ok = $true; status = "REVOKED"; authorizationPersisted = $false; path = $path }
        }
        if (-not (Has-Flag $values "--confirm-disposable-sandbox")) {
            $script:ExitCode = 3
            return New-ErrorResult "operator_confirmation_required" "Run restart authorize-sandbox with --confirm-disposable-sandbox once for this managed-test sandbox."
        }
        $profile = Get-ManagedLaunchProfile $values $config $userRoot
        $authorization = [ordered]@{
            schema = 1
            policy = "explicit-operator-disposable-sandbox"
            scope = "coordinator-owned-managed-test"
            operatorConfirmed = $true
            authorizedUtc = [DateTime]::UtcNow.ToString("o")
            profile = "managed-test"
            executable = $profile.executable
            executableSha256 = $profile.executableSha256
            workingDirectory = $profile.workingDirectory
            arguments = $profile.arguments
            userDataRoot = $profile.userDataRoot
            modConfiguration = $profile.modConfiguration
        }
        Write-JsonAtomic $path $authorization
        $attached = Get-AttachedGameProcesses $profile.executable
        return [ordered]@{
            ok = $true
            status = "AUTHORIZED"
            authorizationPersisted = $true
            path = $path
            scope = $authorization.scope
            attachedProcessDetected = $attached.Count -gt 0
            nextAction = if ($attached.Count -gt 0) { "close the attached process before restart ensure" } else { "use restart ensure" }
        }
    }
    catch {
        $script:ExitCode = 3
        return New-ErrorResult "configuration_error" $_.Exception.Message
    }
}

function Require-SandboxAuthorization([string[]] $values, $config, [string] $userRoot, $profile) {
    $authorization = Get-SandboxAuthorization $values $config $userRoot $profile
    if (-not $authorization.authorized) {
        $script:ExitCode = 3
        return New-ErrorResult $authorization.reason "Run restart authorize-sandbox --confirm-disposable-sandbox once for this validated managed-test profile."
    }
    return $authorization
}

function Get-AttachedGameProcesses([string] $gamePath) {
    try {
        $processName = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFullPath($gamePath))
        if ($processName -notmatch '^RimWorldWin64(?:Steam)?$') { return @() }
        return @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
    }
    catch { return @() }
}

function New-RestartEnsureResult([string] $status, [bool] $requested, [bool] $performed,
    [string] $ownership, [string] $phase, [string] $operatorAction, [string] $nextAction,
    $details = $null) {
    $result = [ordered]@{
        ok = $status -eq "READY"
        status = $status
        restartRequested = $requested
        restartPerformed = $performed
        ownership = $ownership
        ticket = $null
        phase = $phase
        readiness = $null
        operatorActionRequired = -not [string]::IsNullOrWhiteSpace($operatorAction)
        operatorAction = $operatorAction
        nextAction = $nextAction
        contextHandshake = $null
    }
    if ($details) { $result.details = $details }
    return $result
}

function Invoke-RestartEnsure([string[]] $values) {
    $config = Get-ClientConfig
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    if ($null -eq $bridgeRoot -or $null -eq $userRoot) {
        $script:ExitCode = 3
        return New-ErrorResult "configuration_error" "bridge and user roots are required"
    }
    $readiness = Get-Value $values "--readiness" "bridge"
    if ($readiness -notin @("bridge", "game", "map")) { throw "readiness_invalid: use bridge, game, or map" }
    $savePolicy = Get-Value $values "--save-policy" "none"
    if ($savePolicy -notin @("none", "development-copy")) { throw "save_policy_invalid: use none or development-copy" }
    $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
    try {
        $profile = Get-ManagedLaunchProfile $values $config $userRoot
        $authorization = Get-SandboxAuthorization $values $config $userRoot $profile
        if ($authorization.authorized -ne $true) {
            $script:ExitCode = 3
            $result = New-RestartEnsureResult "SANDBOX_AUTHORIZATION_REQUIRED" $false $false "unauthorized" "CONFIGURATION" `
                "Run restart authorize-sandbox --confirm-disposable-sandbox once for this validated managed-test profile." `
                "restart authorize-sandbox --confirm-disposable-sandbox" $authorization
            return $result
        }
        $layout = Get-BridgeLayout $bridgeRoot (Get-Value $values "--layout" "auto")
    }
    catch {
        $script:ExitCode = 3
        return New-ErrorResult "configuration_error" $_.Exception.Message
    }
    $coordinator = Get-CoordinatorInfo $bridgeRoot $layout (Get-Value $values "--coordinator-path") $true
    if (-not $coordinator.available) {
        $script:ExitCode = 3
        return New-ErrorResult ("coordinator_" + $coordinator.status) "A validated coordinator executable is required; use --ensure-runtime-tools or configure --coordinator-path."
    }
    $heartbeat = Invoke-Coordinator "heartbeat" $values
    $ownership = $heartbeat.ownership
    $attached = Get-AttachedGameProcesses $profile.executable
    if (($ownership -and $ownership.running -and -not $ownership.owned) -or $attached.Count -gt 0) {
        $script:ExitCode = 4
        return New-RestartEnsureResult "USER_RESTART_REQUIRED" $false $false "attached" "USER_RESTART_REQUIRED" "Stop the manually attached RimWorld process, then rerun restart ensure." "rerun restart ensure after human restart" $heartbeat
    }
    $launchValues = @(
        "--bridge-root", $bridgeRoot, "--user-root", $userRoot,
        "--agent-id", $agent.value, "--game-path", $profile.executable,
        "--working-directory", $profile.workingDirectory, "--arguments", $profile.arguments,
        "--user-data-root", $profile.userDataRoot, "--mod-configuration", $profile.modConfiguration,
        "--launch-profile", "managed-test", "--owned"
    )
    if ($null -eq $ownership -or -not $ownership.owned -or -not $ownership.running) {
        Clear-LeaseState $userRoot
        $launch = Invoke-Coordinator "launch" $launchValues
        if (-not $launch.ok) {
            $script:ExitCode = 4
            return New-RestartEnsureResult "FAILED" $false $false "none" $launch.phase "Coordinator could not launch the validated profile." "inspect coordinator diagnostics" $launch
        }
    }
    $requestValues = @(
        "--bridge-root", $bridgeRoot, "--user-root", $userRoot,
        "--agent-id", $agent.value, "--package-id", (Get-Value $values "--package-id" "Lan.RimWorldDevBridge"),
        "--readiness", $readiness, "--save-policy", $savePolicy,
        "--reason", (Get-Value $values "--reason" "runtime-verification"),
        "--timeout-ms", (Get-Value $values "--timeout-ms" "120000")
    )
    if (Has-Flag $values "--keep-running") { $requestValues += "--keep-running" }
    Clear-LeaseState $userRoot
    $request = Invoke-Coordinator "request" $requestValues
    if (-not $request.ok) {
        $script:ExitCode = 4
        return New-RestartEnsureResult "FAILED" $false $false "coordinator-owned" $request.phase "Coordinator rejected the restart request." "inspect restart status" $request
    }
    $ticket = $request.ticket
    $waitValues = @($requestValues + @("--ticket", $ticket))
    $wait = Invoke-Coordinator "wait" $waitValues
    $result = New-RestartEnsureResult ([string]$wait.phase) $true ($wait.phase -eq "READY") "coordinator-owned" $wait.phase $null "restart status --ticket $ticket" $wait
    $result.ticket = $ticket
    $result.readiness = $readiness
    $result.contextHandshake = if ($wait.contextHandshake) { $wait.contextHandshake } else { $null }
    if ($wait.phase -ne "READY") { $script:ExitCode = 4 }
    return $result
}

function Invoke-Wake([string[]] $values) {
    if (Has-Flag $values "--start") {
        $ensureValues = @($values)
        if (-not ($values -contains "--readiness")) { $ensureValues += @("--readiness", "bridge") }
        if (-not ($values -contains "--save-policy")) { $ensureValues += @("--save-policy", "none") }
        return Invoke-RestartEnsure $ensureValues
    }
    $config = Get-ClientConfig
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    if ($null -eq $userRoot) { $script:ExitCode = 3; return New-ErrorResult "user_root_unavailable" "Supply --user-root or RIMWORLD_DEVBRIDGE_USER_ROOT." }
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
    $statusPath = Join-Path $userRoot "RimWorld-DevBridge-Status.txt"
    $status = Read-KeyFile $statusPath
    $started = $false
    if ($status["bridge"] -ne "ON" -and [string]::IsNullOrWhiteSpace($status["processId"])) {
        $script:ExitCode = 3
        return New-ErrorResult "process_not_running" "Use restart ensure for a validated coordinator-owned managed-test launch."
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

function Stop-CoordinatorForRoot([string] $coordinatorRoot) {
    $stopped = $false
    try {
        $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -eq 'RimWorldDevBridge.RestartCoordinator.exe' -and
                -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
                $_.CommandLine.IndexOf($coordinatorRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
            })
        foreach ($item in $processes) {
            Stop-Process -Id ([int]$item.ProcessId) -ErrorAction SilentlyContinue
            $stopped = $true
        }
    } catch { return $false }
    return $stopped
}

function Invoke-Coordinator([string] $operation, [string[]] $values, [bool] $EnsureRuntimeTools = $true) {
    $config = Get-ClientConfig
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    if ($null -eq $bridgeRoot -or $null -eq $userRoot) { $script:ExitCode = 3; return New-ErrorResult "path_unavailable" }
    $layout = Get-BridgeLayout $bridgeRoot (Get-Value $values "--layout" "auto")
    $coordinator = Get-CoordinatorInfo $bridgeRoot $layout (Get-Value $values "--coordinator-path") $EnsureRuntimeTools
    if (-not $coordinator.available) {
        $script:ExitCode = 3
        $reason = if ($coordinator.status -eq "missing_build_tooling") { "restart_coordinator_missing_build_tooling" } else { "restart_coordinator_unavailable" }
        return New-ErrorResult $reason "coordinatorStatus=$($coordinator.status); use --ensure-runtime-tools or configure --coordinator-path"
    }
    $coordinatorRoot = Join-Path $userRoot "RimWorld-DevBridge-Coordinator"
    [IO.Directory]::CreateDirectory($coordinatorRoot) | Out-Null
    $exe = $coordinator.path
    $commonArguments = @('--root', $coordinatorRoot, '--user-root', $userRoot, '--bridge-root', $bridgeRoot)
    $probeOutput = & $exe heartbeat @commonArguments --timeout-ms 250 2>&1 | Out-String
    $pipeReady = $LASTEXITCODE -eq 0
    $probeResponse = $null
    if ($pipeReady) { try { $probeResponse = $probeOutput.Trim() | ConvertFrom-Json } catch { $probeResponse = $null } }
    $expectedServerIdentity = "$($coordinator.identity.name)|$($coordinator.identity.version)|$($coordinator.identity.sha256)"
    if ($pipeReady -and ($null -eq $probeResponse -or $probeResponse.CoordinatorIdentity -ne $expectedServerIdentity)) {
        if (-not (Stop-CoordinatorForRoot $coordinatorRoot)) {
            $script:ExitCode = 4
            return New-ErrorResult "restart_coordinator_stale" "A different coordinator build owns the configured coordinator root; stop it explicitly before retrying."
        }
        $pipeReady = $false
        $deadline = [DateTime]::UtcNow.AddSeconds(2)
        do {
            Start-Sleep -Milliseconds 50
            & $exe heartbeat @commonArguments --timeout-ms 100 2>&1 | Out-Null
            $pipeReady = $LASTEXITCODE -eq 0
        } while ($pipeReady -and [DateTime]::UtcNow -lt $deadline)
    }
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
        if ($value -in @("--bridge-root", "--user-root", "--layout", "--coordinator-path", "--launch-profile-path", "--profile-user-root")) { $index++; continue }
        if ($value -in @("--ensure-runtime-tools", "--keep-running", "--json", "--unsafe-debug", "--confirm-disposable-sandbox")) { continue }
        if ($value -eq "--sandbox-authorization-path") { $index++; continue }
        if ($value.StartsWith("--sandbox-authorization-path=", [StringComparison]::OrdinalIgnoreCase)) { continue }
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
        coordinator = [ordered]@{ status = $coordinator.status; identity = $coordinator.identity }
    }
    $result.coordinator.serverIdentity = $response.CoordinatorIdentity
    if (-not [string]::IsNullOrWhiteSpace([string]$response.OwnershipJson)) {
        try { $result.ownership = $response.OwnershipJson | ConvertFrom-Json } catch { $result.ownership = $null }
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
            if ($args.Count -lt 1) { throw "restart_operation_required: use authorize-sandbox, revoke-sandbox, request, status, wait, register, launch, or ensure" }
            if ($args[0] -eq "authorize-sandbox") { $result = Invoke-SandboxAuthorization $args; break }
            if ($args[0] -eq "revoke-sandbox") { $result = Invoke-SandboxAuthorization $args $true; break }
            if ($args[0] -eq "ensure") { $result = Invoke-RestartEnsure $args; break }
            if ($args[0] -eq "launch") {
                try {
                    $launchRoot = Resolve-UserRoot (Get-Value $args "--user-root") $config
                    $launchProfile = Get-ManagedLaunchProfile $args $config $launchRoot
                    $launchAuthorization = Require-SandboxAuthorization $args $config $launchRoot $launchProfile
                    if ($launchAuthorization.authorized -ne $true) { $result = $launchAuthorization; break }
                    if ((Get-AttachedGameProcesses $launchProfile.executable).Count -gt 0) {
                        $script:ExitCode = 4
                        $result = New-ErrorResult "attached_process_user_restart_required" "The configured RimWorld process is attached; no process was claimed or stopped."
                        break
                    }
                }
                catch {
                    $script:ExitCode = 3
                    $result = New-ErrorResult "configuration_error" $_.Exception.Message
                    break
                }
            }
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
