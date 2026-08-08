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

function Set-ActivationRecoveryFields($result) {
    $reason = [string]$result.reason
    $ready = $result.ready -eq $true -or $reason -eq "bridge_ready"
    $attached = $reason -eq "attached_live_process_requires_operator"
    $authorization = $reason -eq "sandbox_authorization_missing"
    $profile = $reason -eq "managed_profile_missing" -or $reason -eq "launch_profile_invalid"
    $inProgress = $result.state -eq "activation_in_progress" -or $reason -eq "activation_timeout"
    if ($ready) {
        if ($reason -eq "activation_ready") { $result.legacyReason = $reason }
        if ([string]$result.phase -eq "BRIDGE_READY") { $result.legacyPhase = $result.phase }
        $result.reason = "bridge_ready"
        $result.phase = "READY"
    }
    $result.activationState = if ($ready) { "ready" } elseif ($inProgress) { "activation_in_progress" } else { "failed" }
    $result.recoverable = if ($ready -or $attached -or $authorization -or $profile) { $false } else { $true }
    $result.waitFor = if ($ready -or $attached -or $authorization -or $profile) { "none" } else { "bridge" }
    $result.keepRunning = -not $attached
    $result.retrySafe = -not ($attached -or $authorization -or $profile)
    $result.operatorActionRequired = $attached
    if ($ready) {
        $result.requiredAction = "none"
        $result.nextAction = "none"
    }
    elseif ($attached) {
        $result.requiredAction = "the process owner must manage the attached RimWorld process"
        $result.nextAction = "wait for the attached process owner or use an explicitly authorized managed-test profile"
    }
    elseif ($authorization) {
        $result.requiredAction = "authorize the validated managed-test profile"
        $result.nextAction = "restart authorize-sandbox --confirm-disposable-sandbox"
    }
    elseif ($profile) {
        $result.requiredAction = "provide a valid managed-test launch profile"
        $result.nextAction = "supply --game-path and an existing --user-data-root"
    }
    elseif ($inProgress) {
        $result.requiredAction = "wait for bridge readiness"
        $result.nextAction = "wait for activation progress or retry restart ensure within the bounded policy"
    }
    else {
        $result.requiredAction = "activate the authorized managed-test instance before retrying"
        $result.nextAction = "restart ensure --readiness bridge --save-policy none --keep-running"
    }
    return $result
}

function Set-InactiveStatusRecoveryFields($result, [string] $userRoot) {
    if ($result.available -eq $true) {
        $result.activationState = "ready"
        $result.recoverable = $false
        $result.waitFor = "none"
        $result.keepRunning = $true
        $result.retrySafe = $true
        $result.operatorActionRequired = $false
        $result.requiredAction = "none"
        $result.nextAction = "none"
        return $result
    }
    $eligible = $false
    try { $eligible = Test-ActivationEligibleReason ([string]$result.reason) } catch { $eligible = $false }
    $activationState = "inactive"
    if ($userRoot) {
        try {
            $activation = Read-ActivationState $userRoot
            if ($activation -and $activation.state -eq "in_progress") { $activationState = "activation_in_progress" }
        } catch { }
    }
    $result.activationState = $activationState
    $result.recoverable = $eligible
    $result.waitFor = if ($eligible) { "bridge" } else { "none" }
    $result.keepRunning = $eligible
    $result.retrySafe = $eligible
    $result.operatorActionRequired = $false
    if ($eligible) {
        $result.requiredAction = "activate the authorized managed-test instance and wait for bridge readiness before retrying read-only verification"
        $result.nextAction = "restart ensure --readiness bridge --save-policy none --keep-running"
    }
    else {
        $result.requiredAction = "inspect the reported runtime status and configuration"
        $result.nextAction = "refresh discover/context after correcting the runtime configuration"
    }
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
        else {
            try { Move-Item -LiteralPath $temporary -Destination $path -Force -ErrorAction Stop }
            catch {
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw }
            }
        }
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

function Get-ClientInstanceId([string] $override, $config) {
    $candidate = $override
    if ([string]::IsNullOrWhiteSpace($candidate) -and $config) { $candidate = [string]$config.clientInstanceId }
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:RIMWORLD_DEVBRIDGE_CLIENT_INSTANCE_ID }
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        if ($candidate -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$') { throw "client_instance_id_invalid" }
        return [ordered]@{ value = $candidate; persisted = $false }
    }
    $directory = if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        Join-Path $env:APPDATA "RimWorldDevBridge"
    } elseif (-not [string]::IsNullOrWhiteSpace($env:XDG_CONFIG_HOME)) {
        Join-Path $env:XDG_CONFIG_HOME "RimWorldDevBridge"
    } else { $null }
    if ($null -eq $directory) { return [ordered]@{ value = "client-" + [Guid]::NewGuid().ToString("N"); persisted = $false } }
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $path = Join-Path $directory "ClientInstanceId.txt"
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $existing = ([IO.File]::ReadAllText($path)).Trim()
        if ($existing -match '^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$') {
            return [ordered]@{ value = $existing; persisted = $true }
        }
    }
    $value = "client-" + [Guid]::NewGuid().ToString("N")
    $temporary = $path + ".tmp." + [Guid]::NewGuid().ToString("N")
    try {
        [IO.File]::WriteAllText($temporary, $value + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            try { Move-Item -LiteralPath $temporary -Destination $path -Force -ErrorAction Stop }
            catch {
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path -LiteralPath $path -PathType Leaf) { $value = ([IO.File]::ReadAllText($path)).Trim() }
    return [ordered]@{ value = $value; persisted = $true }
}

function Get-ConnectionSessionId([string] $override) {
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        if ($override -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$') { throw "connection_session_id_invalid" }
        return $override
    }
    return "connection-" + [Guid]::NewGuid().ToString("N")
}

function Get-ClientCredential([string] $override, $config) {
    $candidate = $override
    if ([string]::IsNullOrWhiteSpace($candidate) -and $config) { $candidate = [string]$config.clientCredential }
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = $env:RIMWORLD_DEVBRIDGE_CLIENT_CREDENTIAL }
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        if ($candidate -notmatch '^[A-Za-z0-9+/=_:-]{16,256}$') { throw "client_credential_invalid" }
        return $candidate
    }
    $directory = if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        Join-Path $env:APPDATA "RimWorldDevBridge"
    } elseif (-not [string]::IsNullOrWhiteSpace($env:XDG_CONFIG_HOME)) {
        Join-Path $env:XDG_CONFIG_HOME "RimWorldDevBridge"
    } else { $null }
    if ($null -eq $directory) { return $null }
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    $path = Join-Path $directory "ClientCredential.txt"
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $existing = ([IO.File]::ReadAllText($path)).Trim()
        if ($existing -match '^[A-Za-z0-9+/=_:-]{16,256}$') { return $existing }
    }
    $value = ([Guid]::NewGuid().ToString("N") + [Guid]::NewGuid().ToString("N"))
    $temporary = $path + ".tmp." + [Guid]::NewGuid().ToString("N")
    try {
        [IO.File]::WriteAllText($temporary, $value + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        try { Move-Item -LiteralPath $temporary -Destination $path -Force -ErrorAction Stop }
        catch { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw } }
    }
    finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
    return ([IO.File]::ReadAllText($path)).Trim()
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

function Get-LeaseToken([string] $userRoot, [string] $explicit, [string] $session, [string] $agentId,
    [string] $clientInstanceId = $null) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return $explicit }
    $state = Read-ClientState $userRoot
    if ($state -and $state.agentId -eq $agentId -and $state.session -eq $session -and
        ([string]::IsNullOrWhiteSpace($clientInstanceId) -or $state.clientInstanceId -eq $clientInstanceId)) {
        return [string]$state.leaseToken
    }
    return $null
}

function Save-LeaseFromResponse([string] $userRoot, $response, [string] $session, [string] $agentId,
    [string] $clientInstanceId = $null) {
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
        clientInstanceId = $clientInstanceId
        session = $session
        leaseToken = $token
        savedUtc = [DateTime]::UtcNow.ToString("o")
    })
}

function Clear-LeaseState([string] $userRoot, [string] $agentId = $null, [string] $clientInstanceId = $null) {
    $path = Get-ClientStatePath $userRoot
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    $state = Read-ClientState $userRoot
    if ([string]::IsNullOrWhiteSpace($agentId) -or [string]::IsNullOrWhiteSpace($clientInstanceId) -or
        $state.agentId -ne $agentId -or $state.clientInstanceId -ne $clientInstanceId) { return }
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}

function Get-BridgeStatus([string] $bridgeRoot, [string] $userRoot, [string] $agentId, $config) {
    $result = [ordered]@{ available = $false; reason = "status_unavailable"; agentId = $agentId }
    $root = Resolve-UserRoot $userRoot $config
    if ($null -eq $root) { $result.reason = "user_data_root_unavailable"; return Set-InactiveStatusRecoveryFields $result $userRoot }
    $statusPath = Join-Path $root "RimWorld-DevBridge-Status.txt"
    if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { return Set-InactiveStatusRecoveryFields $result $root }
    try { $statusFile = Get-Item -LiteralPath $statusPath } catch { return Set-InactiveStatusRecoveryFields $result $root }
    if ([DateTime]::UtcNow - $statusFile.LastWriteTimeUtc -gt [TimeSpan]::FromMinutes(5)) {
        $result.reason = "stale_status"; return Set-InactiveStatusRecoveryFields $result $root
    }
    $status = Read-KeyFile $statusPath
    foreach ($required in @("bridge", "version", "protocol", "schema", "processId", "bootId", "session", "transportGeneration")) {
        if (-not $status.Contains($required) -or [string]::IsNullOrWhiteSpace($status[$required])) {
            $result.reason = "invalid_status_$required"; return Set-InactiveStatusRecoveryFields $result $root
        }
    }
    if ($status["bridge"] -ne "ON") { $result.reason = "bridge_not_active"; return Set-InactiveStatusRecoveryFields $result $root }
    if ([int64]$status["transportGeneration"] -le 0) { $result.reason = "invalid_transport_generation"; return Set-InactiveStatusRecoveryFields $result $root }
    $process = Get-Process -Id ([int]$status["processId"]) -ErrorAction SilentlyContinue
    if ($null -eq $process) { $result.reason = "process_not_running"; return Set-InactiveStatusRecoveryFields $result $root }
    if ([string]::IsNullOrWhiteSpace($status["token"]) -or [string]::IsNullOrWhiteSpace($status["port"])) {
        $result.reason = "transport_credentials_unavailable"; return Set-InactiveStatusRecoveryFields $result $root
    }
    if (-not [string]::IsNullOrWhiteSpace($bridgeRoot)) {
        $manifest = Read-BridgeManifest $bridgeRoot
        if ($status["version"] -ne $manifest["bridge"] -or
            ($status["protocol"] -replace "^v", "") -ne ($manifest["protocol"] -replace "^v", "") -or
            $status["schema"] -ne $manifest["schema"]) {
            $result.reason = "disk_runtime_mismatch"; return Set-InactiveStatusRecoveryFields $result $root
        }
        $corePath = Join-Path $bridgeRoot "1.6\Assemblies\RimWorldDevBridge.dll"
        if ((Test-Path -LiteralPath $corePath -PathType Leaf) -and $status.Contains("coreFingerprint")) {
            $fingerprint = (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash
            $moduleId = [Reflection.Assembly]::ReflectionOnlyLoadFrom($corePath).ManifestModule.ModuleVersionId.ToString("N")
            $reported = "$($status["coreFingerprint"])".ToUpperInvariant()
            if ($fingerprint.ToUpperInvariant() -ne $reported -and $moduleId.ToUpperInvariant() -ne $reported) {
                $result.reason = "core_fingerprint_mismatch"; return Set-InactiveStatusRecoveryFields $result $root
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
    return Set-InactiveStatusRecoveryFields $result $root
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
        ([string]$response.status).ToUpperInvariant() -in @("OK", "PARTIAL")
}

function ConvertTo-BridgeWireValue([string] $value) {
    return [Uri]::EscapeDataString([string]$value)
}

function Get-BridgeTextSha256([string] $value) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes([string]$value))) -replace "-", "").ToUpperInvariant()
    }
    finally { $algorithm.Dispose() }
}

function Test-ActivationEligibleReason([string] $reason) {
    return $reason -in @("bridge_not_active", "status_unavailable", "process_not_running", "stale_status",
        "disk_runtime_mismatch", "core_fingerprint_mismatch", "bridge_did_not_wake")
}

function Invoke-BridgeCommand([string] $command, [string] $argument, [string[]] $values,
    [bool] $mutation = $false, [string] $idempotencyKey = $null, [string] $leaseToken = $null,
    [bool] $AllowActivation = $true) {
    $config = Get-ClientConfig
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
    $clientInstance = Get-ClientInstanceId (Get-Value $values "--client-instance-id") $config
    $clientCredential = Get-ClientCredential (Get-Value $values "--client-credential") $config
    $connectionSessionId = Get-ConnectionSessionId (Get-Value $values "--connection-session-id")
    if ($null -eq $bridgeRoot -or $null -eq $userRoot) {
        $script:ExitCode = 3
        return New-ErrorResult "path_unavailable" "Supply --bridge-root/--user-root or RIMWORLD_DEVBRIDGE_BRIDGE_ROOT/RIMWORLD_DEVBRIDGE_USER_ROOT."
    }
    $status = Get-BridgeStatus $bridgeRoot $userRoot $agent.value $config
    if (-not $status.available -and (Test-ActivationEligibleReason $status.reason)) {
        if ($mutation -or -not $AllowActivation) {
            $status.automaticActivation = $false
            $status.requiredAction = "activate the authorized managed-test instance explicitly before retrying this non-read-only operation"
            $status.nextAction = "restart ensure --readiness bridge --save-policy none --keep-running"
            $status.retrySafe = $false
            $script:ExitCode = 4
            return $status
        }
        else {
            $activation = Invoke-ActivationRecovery $values $status.reason
            if (-not $activation.ready) {
                $script:ExitCode = 4
                $failure = New-ErrorResult $activation.reason "Runtime activation did not complete."
                $failure.activation = $activation
                return Set-ActivationRecoveryFields $failure
            }
            $status = Wait-ForFreshBridgeStatus $bridgeRoot $userRoot $agent.value $config 5000
            if (-not $status.available) {
                $script:ExitCode = 4
                $failure = New-ErrorResult "bridge_load_failed" "Bridge status remained unavailable after activation."
                $failure.activation = $activation
                $failure.status = $status
                return Set-ActivationRecoveryFields $failure
            }
            $freshStatus = Invoke-BridgeCommand "STATUS" "" $values $false $null $null $false
            if (-not (Test-ResponseOk $freshStatus)) {
                $script:ExitCode = 4
                $failure = New-ErrorResult "bridge_load_failed" "Fresh STATUS did not complete after activation."
                $failure.activation = $activation
                $failure.status = $freshStatus
                return Set-ActivationRecoveryFields $failure
            }
        }
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
        $effectiveLease = Get-LeaseToken $userRoot $null $status.session $agent.value $clientInstance.value
    }
    $requestId = [Guid]::NewGuid().ToString("N")
    $correlationId = Get-Value $values "--correlation-id" $requestId
    if ([string]::IsNullOrWhiteSpace($correlationId) -or $correlationId -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$') {
        throw "correlation_id_invalid"
    }
    $options = @("format=json", "agentId=$(ConvertTo-BridgeWireValue $agent.value)",
        "clientInstanceId=$(ConvertTo-BridgeWireValue $clientInstance.value)",
        "clientCredential=$(ConvertTo-BridgeWireValue $clientCredential)",
        "connectionSessionId=$(ConvertTo-BridgeWireValue $connectionSessionId)",
        "correlationId=$(ConvertTo-BridgeWireValue $correlationId)",
        "workspaceId=$(ConvertTo-BridgeWireValue $workspaceId)", "timeoutMs=$timeout")
    $participantId = Get-Value $values "--participant-id"
    if (-not [string]::IsNullOrWhiteSpace($participantId)) {
        $options += "participantId=$(ConvertTo-BridgeWireValue $participantId)"
    }
    $operationId = Get-Value $values "--operation-id"
    if (-not [string]::IsNullOrWhiteSpace($operationId)) {
        $options += "operationId=$(ConvertTo-BridgeWireValue $operationId)"
    }
    foreach ($identityOption in @(
            "operation-kind", "desired-state", "compatibility-key", "runtime-slot-id", "deployment-id",
            "artifact-fingerprint", "loaded-assembly-fingerprint", "managed-profile", "rimworld-version",
            "mod-set-fingerprint", "mod-load-order-fingerprint", "source-build-identity",
             "expected-core-fingerprint", "expected-adapter-fingerprint", "configuration-fingerprint",
             "user-root-fingerprint", "save-target", "map-target", "requires-process-replacement",
             "lifecycle-generation", "mutation-scope", "expected-process-id",
             "expected-process-start-identity", "expected-process-session-id",
             "expected-process-lifecycle-generation")) {
        $identityValue = Get-Value $values "--$identityOption"
        if (-not [string]::IsNullOrWhiteSpace($identityValue)) {
            $parts = @($identityOption -split "-")
            $wireName = $parts[0]
            for ($partIndex = 1; $partIndex -lt $parts.Count; $partIndex++) {
                $wireName += $parts[$partIndex].Substring(0, 1).ToUpperInvariant() + $parts[$partIndex].Substring(1)
            }
            $options += "$wireName=$(ConvertTo-BridgeWireValue $identityValue)"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($effectiveLease)) {
        $options += "lease=$(ConvertTo-BridgeWireValue $effectiveLease)"
    }
    if (-not [string]::IsNullOrWhiteSpace($idempotencyKey)) {
        $options += "idempotency=$(ConvertTo-BridgeWireValue $idempotencyKey)"
    }
    if (Has-Flag $values "--allow-expensive") { $options += "allowExpensive=true" }
    foreach ($option in $values) {
        if ($option -like "--option=*") { $options += $option.Substring(9) }
    }
    if ($null -eq $argument) { $argument = "" }
    if ($argument -match '[|\r\n]') { throw "bridge_argument_invalid: pipe and newline characters are not supported" }
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
            Save-LeaseFromResponse $userRoot $parsed $status.session $agent.value $clientInstance.value
        }
        if ($command -eq "REVOKE_WRITE_LEASE" -and $parsed -and (Test-ResponseOk $parsed)) {
            Clear-LeaseState $userRoot $agent.value $clientInstance.value
        }
        $response = Convert-BridgeResponse $rawResponse $agent.value $idempotencyKey
        if ($response -is [System.Management.Automation.PSCustomObject]) {
            Add-Member -InputObject $response -NotePropertyName clientInstanceId -NotePropertyValue $clientInstance.value -Force
            Add-Member -InputObject $response -NotePropertyName connectionSessionId -NotePropertyValue $connectionSessionId -Force
            Add-Member -InputObject $response -NotePropertyName correlationId -NotePropertyValue $correlationId -Force
            if (-not [string]::IsNullOrWhiteSpace($participantId)) {
                Add-Member -InputObject $response -NotePropertyName participantId -NotePropertyValue $participantId -Force
            }
        }
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
            "goal ensure|status|wait|cancel|checkpoint|resume",
            "review request|list|get|resolve|cancel|wait|checkpoint|resume",
            "validate --layout auto|source|package", "help"
        )
        exitCodes = [ordered]@{ success = 0; invalidArguments = 2; pathOrUnavailable = 3; transport = 4; stale = 5; bridgeRejected = 6 }
        secrets = "transport and lease tokens are redacted; use --unsafe-debug only for explicit local debugging"
        activation = "bridge_not_active is recoverable. Activate the authorized managed-test instance and wait for bridge readiness before abandoning runtime verification."
        activationOptions = @("--startup-timeout-ms <100..600000>", "--progress-interval-ms <100..10000>")
        responseContract = [ordered]@{
            activationState = @("inactive", "activation_in_progress", "ready", "failed")
            waitFor = @("none", "bridge", "game", "map")
            canonicalReady = "reason=bridge_ready;phase=READY"
            canonicalAttached = "attached_live_process_requires_operator"
            fields = @("activationState", "waitFor", "recoverable", "requiredAction", "keepRunning", "retrySafe", "operatorActionRequired", "nextAction")
            automaticActivation = @("discover", "context", "describe", "read", "repo context", "lease inspect")
            noAutomaticActivation = @("call", "mutate", "cancel", "lease acquire", "lease renew", "lease release", "adapter reload")
        }
        reviewQueue = [ordered]@{
            commands = @("request", "list", "get", "resolve", "cancel", "wait", "checkpoint", "resume")
            categories = @("human_review", "human_approval", "hard_blocker")
            defaultResponseWindowMs = 60000
            awaitingState = "READY_AWAITING_HUMAN"
            safety = "Human review and safety approval never grant mutation authority, attached-process control, or a write lease."
        }
        managedLaunch = [ordered]@{
            maxAttempts = "--max-launch-attempts <1..5> (default 2)"
            backoff = "--launch-backoff-ms <0..10000> (default 500)"
            deadCoordinatorOwnedProcess = "automatically validate identity, clear stale ownership, and retry; never return USER_RESTART_REQUIRED for a dead coordinator-owned PID"
            states = @("managed_process_exited_before_ready", "stale_managed_ownership_recovered", "managed_launch_retrying", "managed_launch_failed", "bridge_handshake_timeout", "bridge_load_failed", "launch_profile_invalid", "attached_live_process_requires_operator", "bridge_ready")
        }
        goalOperations = [ordered]@{
            states = @("queued", "running", "succeeded", "failed", "cancelled", "checkpointed")
            phases = @("QUEUED", "ACTIVATING", "WAITING_FOR_BRIDGE", "WAITING_FOR_GAME", "WAITING_FOR_MAP", "READY", "MAP_READY", "TEST_READY", "FAILED", "CANCELLED", "READY_AWAITING_HUMAN")
            desiredStates = @("bridge", "map", "test_ready")
            commands = @("ensure", "status", "wait", "cancel", "checkpoint", "resume")
            fields = @("goalId", "operationId", "operationState", "phase", "desiredState", "startedUtc", "updatedUtc", "overallDeadlineUtc", "timeoutMs", "noProgressTimeoutMs", "lastProgressUtc", "progressSequence", "pid", "sessionId", "lifecycleGeneration", "coreFingerprint", "contextFresh", "recoverable", "requiredAction", "waitFor", "keepRunning", "retrySafe", "operatorActionRequired", "nextAction", "resourcesReleased", "evidence")
            safety = "Goal orchestration uses only authorized coordinator-owned managed-test instances; it never grants mutation authority or a write lease and never claims an attached process."
        }
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
        $moved = $false
        for ($attempt = 0; $attempt -lt 8 -and -not $moved; $attempt++) {
            try {
                Move-Item -LiteralPath $temporary -Destination $path -Force -ErrorAction Stop
                $moved = $true
            }
            catch [IO.IOException] {
                if ($attempt -eq 7) { throw }
                Start-Sleep -Milliseconds 25
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Get-ActivationTimeoutMs([string[]] $values, $config) {
    $text = Get-Value $values "--startup-timeout-ms"
    if ([string]::IsNullOrWhiteSpace($text) -and $config) { $text = [string]$config.activationStartupTimeoutMs }
    if ([string]::IsNullOrWhiteSpace($text)) { $text = "120000" }
    $timeout = 0
    if (-not [int]::TryParse($text, [ref]$timeout) -or $timeout -lt 100 -or $timeout -gt 600000) {
        throw "startup_timeout_ms_invalid: use a value between 100 and 600000"
    }
    return $timeout
}

function Get-ActivationProgressIntervalMs([string[]] $values, $config) {
    $text = Get-Value $values "--progress-interval-ms"
    if ([string]::IsNullOrWhiteSpace($text) -and $config) { $text = [string]$config.activationProgressIntervalMs }
    if ([string]::IsNullOrWhiteSpace($text)) { $text = "1000" }
    $interval = 0
    if (-not [int]::TryParse($text, [ref]$interval) -or $interval -lt 100 -or $interval -gt 10000) {
        throw "progress_interval_ms_invalid: use a value between 100 and 10000"
    }
    return $interval
}

function Get-ActivationStatePath([string] $userRoot) {
    return Join-Path $userRoot "RimWorld-DevBridge-Activation.json"
}

function Wait-ForFreshBridgeStatus([string] $bridgeRoot, [string] $userRoot,
    [string] $agentId, $config, [int] $timeoutMs = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds([Math]::Max(100, [Math]::Min(600000, $timeoutMs)))
    $last = $null
    do {
        $last = Get-BridgeStatus $bridgeRoot $userRoot $agentId $config
        if ($last.available) { return $last }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return $last
}

function Get-ActivationLockPath([string] $userRoot) {
    return Join-Path $userRoot "RimWorld-DevBridge-Activation.lock"
}

function Read-ActivationState([string] $userRoot) {
    try { return Read-JsonFile (Get-ActivationStatePath $userRoot) }
    catch { return $null }
}

function Write-ActivationState([string] $userRoot, [string] $state, [string] $phase,
    [string] $reason, [string] $operationId, [string] $startedUtc, $result = $null) {
    $value = [ordered]@{
        schema = 1
        state = $state
        phase = $phase
        reason = $reason
        operationId = $operationId
        startedUtc = $startedUtc
        updatedUtc = [DateTime]::UtcNow.ToString("o")
    }
    if ($null -ne $result) { $value.result = $result }
    Write-JsonAtomic (Get-ActivationStatePath $userRoot) $value
    return $value
}

function Get-ReviewRoot([string] $userRoot) {
    $root = Join-Path $userRoot "RimWorld-DevBridge-Review"
    [IO.Directory]::CreateDirectory($root) | Out-Null
    return $root
}

function Get-ReviewLockPath([string] $userRoot) {
    return Join-Path (Get-ReviewRoot $userRoot) "queue.lock"
}

function Get-ReviewRequestPath([string] $userRoot, [string] $requestId) {
    return Join-Path (Get-ReviewRoot $userRoot) ("request-" + $requestId + ".json")
}

function Test-SafeReviewId([string] $value) {
    return -not [string]::IsNullOrWhiteSpace($value) -and $value.Length -le 96 -and
        $value -match '^[A-Za-z0-9._-]+$'
}

function Invoke-ReviewLocked([string] $userRoot, [scriptblock] $action) {
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    $lock = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $lock = [IO.FileStream]::new((Get-ReviewLockPath $userRoot), [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            break
        } catch [IO.IOException] { Start-Sleep -Milliseconds 25 }
        catch [UnauthorizedAccessException] { Start-Sleep -Milliseconds 25 }
    }
    if ($null -eq $lock) { throw "review_queue_busy: retry the review operation" }
    try { return & $action }
    finally { $lock.Dispose() }
}

function Get-ReviewRequests([string] $userRoot) {
    $files = @(Get-ChildItem -LiteralPath (Get-ReviewRoot $userRoot) -Filter "request-*.json" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 256)
    $requests = @()
    foreach ($file in $files) {
        try {
            $request = Read-JsonFile $file.FullName
            if ($request) { $requests += $request }
        } catch { }
    }
    return $requests
}

function Get-ReviewRequest([string] $userRoot, [string] $requestId) {
    if (-not (Test-SafeReviewId $requestId)) { throw "review_request_id_invalid" }
    $request = Read-JsonFile (Get-ReviewRequestPath $userRoot $requestId)
    if ($null -eq $request) { throw "review_request_not_found" }
    return $request
}

function Save-ReviewRequest([string] $userRoot, $request) {
    Write-JsonAtomic (Get-ReviewRequestPath $userRoot ([string]$request.requestId)) $request
    return $request
}

function Set-ReviewProperty($request, [string] $name, $value) {
    if ($null -eq $request.PSObject.Properties[$name]) {
        $request | Add-Member -NotePropertyName $name -NotePropertyValue $value
    }
    else { $request.$name = $value }
    return $request
}

function Update-ReviewCheckpoint($request, [string] $reason) {
    $request.state = "READY_AWAITING_HUMAN"
    Set-ReviewProperty $request "checkpointedUtc" ([DateTime]::UtcNow.ToString("o")) | Out-Null
    Set-ReviewProperty $request "checkpointReason" (Redact-Text $reason) | Out-Null
    Set-ReviewProperty $request "checkpoint" ([ordered]@{
        requestId = $request.requestId
        taskId = $request.taskId
        state = $request.state
        resumeOperation = $request.resumeOperation
        completedWork = $request.completedWork
        remainingDependentWork = $request.remainingDependentWork
        independentWork = $request.independentWork
        resourcesReleased = $true
        runtimePreserved = [bool]$request.runtimePreserved
    }) | Out-Null
    return $request
}

function Refresh-ReviewRequest([string] $userRoot, $request) {
    $now = [DateTime]::UtcNow
    if ($request.state -eq "WAITING_FOR_HUMAN" -and $request.expiresUtc) {
        $expires = [DateTime]::MinValue
        if ([DateTime]::TryParse([string]$request.expiresUtc, [ref]$expires) -and $now -ge $expires) {
            $request.state = "EXPIRED"
            Set-ReviewProperty $request "resolvedUtc" ($now.ToString("o")) | Out-Null
            Set-ReviewProperty $request "resolution" "expired_by_policy" | Out-Null
            Set-ReviewProperty $request "resourcesReleased" $true | Out-Null
            Set-ReviewProperty $request "checkpoint" ([ordered]@{
                requestId = $request.requestId
                taskId = $request.taskId
                state = $request.state
                resumeOperation = $request.resumeOperation
                completedWork = $request.completedWork
                remainingDependentWork = $request.remainingDependentWork
                independentWork = $request.independentWork
                resourcesReleased = $true
                runtimePreserved = [bool]$request.runtimePreserved
            }) | Out-Null
            return $request
        }
    }
    if ($request.state -eq "WAITING_FOR_HUMAN" -and $request.responseDeadlineUtc) {
        $deadline = [DateTime]::MinValue
        if ([DateTime]::TryParse([string]$request.responseDeadlineUtc, [ref]$deadline) -and $now -ge $deadline) {
            return Update-ReviewCheckpoint $request "response_window_expired; no optional human feedback arrived"
        }
    }
    return $request
}

function Get-ReviewText([string[]] $values, [string] $name, [string] $default = "") {
    return Redact-Text (Get-Value $values $name $default)
}

function New-ReviewRequest([string] $userRoot, [string[]] $values, $agent) {
    $category = (Get-Value $values "--category" "human_review").ToLowerInvariant()
    if ($category -notin @("human_review", "human_approval", "hard_blocker")) {
        throw "review_category_invalid"
    }
    $taskId = Get-ReviewText $values "--task-id"
    $question = Get-ReviewText $values "--question"
    $resume = Get-ReviewText $values "--resume-operation"
    if ([string]::IsNullOrWhiteSpace($taskId) -or [string]::IsNullOrWhiteSpace($question) -or
        [string]::IsNullOrWhiteSpace($resume)) { throw "review_required_fields_missing" }
    $requestId = Get-Value $values "--request-id" ([Guid]::NewGuid().ToString("N"))
    if (-not (Test-SafeReviewId $requestId) -or -not (Test-SafeReviewId $taskId)) { throw "review_id_invalid" }
    $option1 = Get-ReviewText $values "--option-1"
    $option2 = Get-ReviewText $values "--option-2"
    $option3 = Get-ReviewText $values "--option-3"
    if ($category -ne "hard_blocker" -and ([string]::IsNullOrWhiteSpace($option1) -or [string]::IsNullOrWhiteSpace($option2))) {
        throw "review_options_required: provide option-1 and option-2"
    }
    $responseMs = 60000
    [int]::TryParse((Get-Value $values "--response-timeout-ms" "60000"), [ref]$responseMs) | Out-Null
    if ($responseMs -lt 1000 -or $responseMs -gt 600000) { throw "review_response_timeout_invalid" }
    $now = [DateTime]::UtcNow
    $dedupKey = Get-ReviewText $values "--dedup-key" ($taskId + "|" + $category + "|" + $question)
    $existing = @(Get-ReviewRequests $userRoot | Where-Object {
        $_.deduplicationKey -eq $dedupKey -and $_.state -in @("WAITING_FOR_HUMAN", "READY_AWAITING_HUMAN", "RESOLVED")
    } | Select-Object -First 1)
    if ($existing.Count -gt 0) {
        $existing[0] | Add-Member -NotePropertyName deduplicated -NotePropertyValue $true -Force
        return $existing[0]
    }
    $request = [ordered]@{
        schema = 1
        requestId = $requestId
        taskId = $taskId
        category = $category
        question = $question
        options = @($option1, $option2, $option3 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        recommendedDefault = Get-ReviewText $values "--recommended"
        screenshotReferences = @(Get-ReviewText $values "--screenshot-ref")
        artifactReferences = @(Get-ReviewText $values "--artifact-ref")
        completedWork = Get-ReviewText $values "--completed-work"
        verificationEvidence = Get-ReviewText $values "--verification-evidence"
        remainingDependentWork = Get-ReviewText $values "--dependent-work"
        independentWork = Get-ReviewText $values "--independent-work"
        runtimePreserved = Has-Flag $values "--preserve-runtime"
        branch = Get-ReviewText $values "--branch" "unknown"
        commit = Get-ReviewText $values "--commit" "unknown"
        dirtyState = Get-ReviewText $values "--dirty-state" "unknown"
        createdByAgentId = if ($agent) { [string]$agent.value } else { "unknown" }
        createdUtc = $now.ToString("o")
        responseDeadlineUtc = $now.AddMilliseconds($responseMs).ToString("o")
        expiresUtc = Get-ReviewText $values "--expires-utc"
        deduplicationKey = $dedupKey
        resumeOperation = $resume
        state = "WAITING_FOR_HUMAN"
        authorization = [ordered]@{ authorizesMutation = $false; authorizesAttachedProcess = $false; grantsWriteLease = $false }
    }
    return $request
}

function Invoke-Review([string[]] $values, $agent, [string] $userRoot) {
    $action = if ($values.Count -gt 0) { $values[0].ToLowerInvariant() } else { "list" }
    $remaining = if ($values.Count -gt 1) { @($values[1..($values.Count - 1)]) } else { @() }
    switch ($action) {
        "request" {
            $request = Invoke-ReviewLocked $userRoot {
                $created = New-ReviewRequest $userRoot $remaining $agent
                if (-not $created.deduplicated) { Save-ReviewRequest $userRoot $created | Out-Null }
                $created
            }
            return [ordered]@{ ok = $true; operation = "review.request"; request = $request; resourcesReleased = $true }
        }
        "list" {
            $requests = @(Invoke-ReviewLocked $userRoot {
                @(Get-ReviewRequests $userRoot | ForEach-Object {
                    $fresh = Refresh-ReviewRequest $userRoot $_
                    if ($fresh.state -ne $_.state) { Save-ReviewRequest $userRoot $fresh | Out-Null }
                    $fresh
                })
            })
            return [ordered]@{ ok = $true; operation = "review.list"; requests = @($requests); count = $requests.Count }
        }
        "get" {
            $request = Invoke-ReviewLocked $userRoot {
                $current = Get-ReviewRequest $userRoot (Get-Value $remaining "--request-id")
                $current = Refresh-ReviewRequest $userRoot $current
                Save-ReviewRequest $userRoot $current | Out-Null
                $current
            }
            return [ordered]@{ ok = $true; operation = "review.get"; request = $request }
        }
        "checkpoint" {
            $requestId = Get-Value $remaining "--request-id"
            $request = Invoke-ReviewLocked $userRoot {
                $current = Get-ReviewRequest $userRoot $requestId
                $current = Update-ReviewCheckpoint $current (Get-ReviewText $remaining "--reason" "autonomous work paused for human dependency")
                Set-ReviewProperty $current "checkpointEvidence" (Get-ReviewText $remaining "--checkpoint-evidence") | Out-Null
                Save-ReviewRequest $userRoot $current | Out-Null
                $current
            }
            return [ordered]@{ ok = $true; operation = "review.checkpoint"; request = $request; resourcesReleased = $true }
        }
        "resolve" {
            $request = Invoke-ReviewLocked $userRoot {
                $current = Get-ReviewRequest $userRoot (Get-Value $remaining "--request-id")
                $current.state = "RESOLVED"
                Set-ReviewProperty $current "resolvedUtc" ([DateTime]::UtcNow.ToString("o")) | Out-Null
                Set-ReviewProperty $current "selectedOption" (Get-ReviewText $remaining "--selected-option") | Out-Null
                Set-ReviewProperty $current "answer" (Get-ReviewText $remaining "--answer") | Out-Null
                Set-ReviewProperty $current "resolution" (Get-ReviewText $remaining "--resolution" "human_response") | Out-Null
                $current.authorization = [ordered]@{ authorizesMutation = $false; authorizesAttachedProcess = $false; grantsWriteLease = $false }
                Save-ReviewRequest $userRoot $current | Out-Null
                $current
            }
            return [ordered]@{ ok = $true; operation = "review.resolve"; request = $request; resumeOperation = $request.resumeOperation; authorization = $request.authorization }
        }
        "cancel" {
            $request = Invoke-ReviewLocked $userRoot {
                $current = Get-ReviewRequest $userRoot (Get-Value $remaining "--request-id")
                $current.state = "CANCELLED"
                Set-ReviewProperty $current "resolvedUtc" ([DateTime]::UtcNow.ToString("o")) | Out-Null
                Set-ReviewProperty $current "resolution" (Get-ReviewText $remaining "--reason" "cancelled") | Out-Null
                Save-ReviewRequest $userRoot $current | Out-Null
                $current
            }
            return [ordered]@{ ok = $true; operation = "review.cancel"; request = $request; resourcesReleased = $true }
        }
        "resume" {
            $request = Invoke-ReviewLocked $userRoot {
                $current = Get-ReviewRequest $userRoot (Get-Value $remaining "--request-id")
                $current = Refresh-ReviewRequest $userRoot $current
                Save-ReviewRequest $userRoot $current | Out-Null
                $current
            }
            return [ordered]@{ ok = $request.state -eq "RESOLVED"; operation = "review.resume"; request = $request; canResume = $request.state -eq "RESOLVED"; resumeOperation = $request.resumeOperation; authorization = [ordered]@{ authorizesMutation = $false; authorizesAttachedProcess = $false; grantsWriteLease = $false } }
        }
        "wait" {
            $requestId = Get-Value $remaining "--request-id"
            $timeoutMs = 60000
            [int]::TryParse((Get-Value $remaining "--timeout-ms" "60000"), [ref]$timeoutMs) | Out-Null
            if ($timeoutMs -lt 100 -or $timeoutMs -gt 600000) { throw "review_wait_timeout_invalid" }
            $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
            do {
                $request = Invoke-ReviewLocked $userRoot {
                    $current = Get-ReviewRequest $userRoot $requestId
                    $current = Refresh-ReviewRequest $userRoot $current
                    Save-ReviewRequest $userRoot $current | Out-Null
                    $current
                }
                if ($request.state -ne "WAITING_FOR_HUMAN") {
                    return [ordered]@{ ok = $request.state -eq "RESOLVED"; operation = "review.wait"; request = $request; awaitingHuman = $request.state -eq "READY_AWAITING_HUMAN"; resourcesReleased = $true }
                }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            $request = Invoke-ReviewLocked $userRoot {
                $current = Get-ReviewRequest $userRoot $requestId
                $current = Update-ReviewCheckpoint $current "response window elapsed; autonomous work is complete"
                Save-ReviewRequest $userRoot $current | Out-Null
                $current
            }
            return [ordered]@{ ok = $true; operation = "review.wait"; request = $request; awaitingHuman = $true; resourcesReleased = $true; nextAction = "review resume --request-id $requestId after resolution" }
        }
        default { throw "review_operation_invalid: use request, list, get, resolve, cancel, wait, checkpoint, or resume" }
    }
}

function Try-OpenActivationLock([string] $userRoot) {
    try {
        return [IO.FileStream]::new((Get-ActivationLockPath $userRoot), [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] { return $null }
    catch [UnauthorizedAccessException] { return $null }
}

function Write-ActivationProgress([string] $state, [string] $phase, [int64] $elapsedMs,
    [string] $reason = $null, [bool] $coalesced = $false) {
    $progress = [ordered]@{
        event = "activation_progress"
        state = $state
        phase = $phase
        elapsedMs = $elapsedMs
        coalesced = $coalesced
        timestampUtc = [DateTime]::UtcNow.ToString("o")
    }
    if (-not [string]::IsNullOrWhiteSpace($reason)) { $progress.reason = $reason }
    [Console]::Error.WriteLine(($progress | ConvertTo-Json -Compress))
}

function New-ActivationEnsureValues([string[]] $values, [string] $bridgeRoot, [string] $userRoot,
    [int] $timeoutMs) {
    $result = @("--bridge-root", $bridgeRoot, "--user-root", $userRoot)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $allowed = @(
        "--agent-id", "--client-instance-id", "--connection-session-id", "--participant-id", "--correlation-id",
         "--operation-id", "--operation-kind", "--desired-state", "--compatibility-key", "--runtime-slot-id", "--deployment-id",
         "--artifact-fingerprint", "--loaded-assembly-fingerprint", "--required-adapter-fingerprint",
         "--expected-process-id", "--expected-process-start-identity", "--expected-process-session-id",
         "--expected-process-lifecycle-generation",
         "--mod-set-fingerprint", "--mod-load-order-fingerprint", "--source-build-identity", "--rimworld-version",
         "--save-target", "--map-target", "--package-id", "--layout", "--coordinator-path", "--launch-profile-path",
        "--profile-user-root", "--game-path", "--working-directory", "--arguments", "--game-arguments",
        "--mod-configuration", "--sandbox-authorization-path", "--reason",
        "--max-launch-attempts", "--launch-backoff-ms", "--required-core-fingerprint",
        "--readiness", "--target-postcondition", "--requires-new-process", "--allow-supersede",
        "--requested-pid", "--requested-session-id", "--requested-lifecycle-generation"
    )
    for ($index = 0; $index -lt $values.Count; $index++) {
        $value = [string]$values[$index]
        $name = if ($value.Contains("=")) { $value.Substring(0, $value.IndexOf("=")) } else { $value }
        if ($allowed -notcontains $name) { continue }
        if (-not $seen.Add($name)) {
            if (-not $value.Contains("=") -and $index + 1 -lt $values.Count -and
                -not ([string]$values[$index + 1]).StartsWith("--")) { $index++ }
            continue
        }
        $result += $value
        $nextValue = if ($index + 1 -lt $values.Count) { [string]$values[$index + 1] } else { "" }
        if (-not $value.Contains("=") -and $index + 1 -lt $values.Count -and
            -not $nextValue.StartsWith("--", [StringComparison]::Ordinal)) {
            $index++
            $result += $values[$index]
        }
    }
    $requestedReadiness = Get-Value $values "--readiness" "bridge"
    if ($requestedReadiness -notin @("bridge", "game", "map")) { $requestedReadiness = "bridge" }
    if (-not ($result | Where-Object { $_ -eq "--readiness" -or $_ -like "--readiness=*" })) {
        $result += @("--readiness", $requestedReadiness)
    }
    $result += @("--save-policy", "none", "--keep-running", "--timeout-ms", [string]$timeoutMs)
    $targetPostcondition = Get-Value $values "--target-postcondition" $requestedReadiness
    if (-not ($result -contains "--target-postcondition") -and
        -not ($result | Where-Object { [string]$_ -like "--target-postcondition=*" })) {
        $result += @("--target-postcondition", $targetPostcondition)
    }
    if (-not ($result -contains "--max-launch-attempts") -and
        -not ($result | Where-Object { [string]$_ -like "--max-launch-attempts=*" })) {
        $result += @("--max-launch-attempts", (Get-Value $values "--max-launch-attempts" "2"))
    }
    if (-not ($result -contains "--launch-backoff-ms") -and
        -not ($result | Where-Object { [string]$_ -like "--launch-backoff-ms=*" })) {
        $result += @("--launch-backoff-ms", (Get-Value $values "--launch-backoff-ms" "500"))
    }
    return $result
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
    if ([string]::IsNullOrWhiteSpace($arguments) -and $existing) { $arguments = [string]$existing.arguments }
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
    $details = $null, [string] $failureReason = $null) {
    $attached = $failureReason -eq "attached_live_process_requires_operator" -or $status -eq "USER_RESTART_REQUIRED"
    $authorization = $failureReason -eq "sandbox_authorization_missing" -or $status -eq "SANDBOX_AUTHORIZATION_REQUIRED"
    $configuration = $status -eq "CONFIGURATION" -or $status -eq "configuration_error"
    $ready = $status -eq "READY"
    $inProgress = -not $ready -and -not $attached -and -not $authorization -and -not $configuration -and $requested
    $result = [ordered]@{
        ok = $ready
        status = $status
        restartRequested = $requested
        restartPerformed = $performed
        ownership = $ownership
        ticket = $null
        phase = $phase
        readiness = $null
        operatorActionRequired = $attached -or -not [string]::IsNullOrWhiteSpace($operatorAction)
        operatorAction = $operatorAction
        nextAction = $nextAction
        contextHandshake = $null
        activationState = if ($ready) { "ready" } elseif ($inProgress) { "activation_in_progress" } else { "failed" }
        recoverable = -not ($ready -or $attached -or $authorization -or $configuration)
        requiredAction = if ($ready) { "none" } elseif ($attached) { "the process owner must manage the attached RimWorld process" } elseif ($authorization) { "authorize the validated managed-test profile" } elseif ($configuration) { "correct the managed-test launch configuration" } elseif ($inProgress) { "wait for bridge readiness" } else { "retry restart ensure within the bounded managed-launch policy" }
        waitFor = if ($ready -or $attached -or $authorization -or $configuration) { "none" } else { "bridge" }
        keepRunning = -not $attached
        retrySafe = -not ($attached -or $authorization -or $configuration)
    }
    if ($result.operatorActionRequired -and [string]::IsNullOrWhiteSpace($result.nextAction)) { $result.nextAction = "wait for the process owner or use an explicitly authorized managed-test profile" }
    if (-not [string]::IsNullOrWhiteSpace($failureReason)) { $result.reason = $failureReason }
    if ($details) { $result.details = $details }
    return $result
}

function Invoke-RestartEnsure([string[]] $values, [scriptblock] $ProgressCallback = $null) {
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
    $clientInstance = Get-ClientInstanceId (Get-Value $values "--client-instance-id") $config
    try {
        $profile = Get-ManagedLaunchProfile $values $config $userRoot
        $authorization = Get-SandboxAuthorization $values $config $userRoot $profile
        if ($authorization.authorized -ne $true) {
            $script:ExitCode = 3
            $result = New-RestartEnsureResult "SANDBOX_AUTHORIZATION_REQUIRED" $false $false "unauthorized" "CONFIGURATION" `
                "Run restart authorize-sandbox --confirm-disposable-sandbox once for this validated managed-test profile." `
                "restart authorize-sandbox --confirm-disposable-sandbox" $authorization "sandbox_authorization_missing"
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
    $userRootFingerprint = Get-BridgeTextSha256 ([IO.Path]::GetFullPath($userRoot))
    $runtimeSlotId = Get-Value $values "--runtime-slot-id"
    if ([string]::IsNullOrWhiteSpace($runtimeSlotId)) { $runtimeSlotId = "slot-" + $userRootFingerprint.Substring(0, 16) }
    $heartbeatValues = @($values)
    if (-not ($values -contains "--runtime-slot-id") -and
        -not (@($values | Where-Object { ([string]$_).StartsWith("--runtime-slot-id=", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0)) {
        $heartbeatValues += @("--runtime-slot-id", $runtimeSlotId)
    }
    $heartbeat = Invoke-Coordinator "heartbeat" $heartbeatValues
    $ownership = $heartbeat.ownership
    $ownedProcessId = 0
    if ($ownership -and $ownership.running -and $ownership.owned) {
        $ownedProcessId = [int]$ownership.ProcessId
    }
    $attached = @(Get-AttachedGameProcesses $profile.executable |
        Where-Object { $_.Id -ne $ownedProcessId })
    if (($ownership -and $ownership.running -and -not $ownership.owned) -or $attached.Count -gt 0) {
        $script:ExitCode = 4
        return New-RestartEnsureResult "USER_RESTART_REQUIRED" $false $false "attached" "USER_RESTART_REQUIRED" "A human or external orchestrator must manage the attached RimWorld process." "rerun restart ensure after the attached process is stopped by its owner" $heartbeat "attached_live_process_requires_operator"
    }
    $launchValues = @(
        "--bridge-root", $bridgeRoot, "--user-root", $userRoot,
        "--agent-id", $agent.value, "--game-path", $profile.executable,
        "--working-directory", $profile.workingDirectory, "--arguments", $profile.arguments,
        "--user-data-root", $profile.userDataRoot, "--mod-configuration", $profile.modConfiguration,
        "--launch-profile", "managed-test", "--owned"
    )
    Clear-LeaseState $userRoot $agent.value $clientInstance.value
    $preStatus = Get-BridgeStatus $bridgeRoot $userRoot $agent.value $config
    $requiresNewProcess = (Has-Flag $values "--requires-new-process") -or
        [bool]($ownership -and $ownership.running -and $ownership.owned)
    $targetPostcondition = Get-Value $values "--target-postcondition" $readiness
    if ($targetPostcondition -notin @("bridge", "game", "map", "test_ready")) {
        $script:ExitCode = 3
        return New-ErrorResult "target_postcondition_invalid" "Use bridge, game, map, or test_ready."
    }
    $explicitCoreFingerprint = Get-Value $values "--required-core-fingerprint"
    if (-not [string]::IsNullOrWhiteSpace($explicitCoreFingerprint) -and
        $explicitCoreFingerprint -notmatch '^[A-Fa-f0-9]{64}$') {
        $script:ExitCode = 3
        return New-ErrorResult "required_core_fingerprint_invalid" "The required core fingerprint must be a SHA-256 value."
    }
    $corePath = Join-Path $bridgeRoot "1.6\Assemblies\RimWorldDevBridge.dll"
    $localCoreFingerprint = if (Test-Path -LiteralPath $corePath -PathType Leaf) {
        (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash
    } else { "" }
    $requiredCoreFingerprint = if (-not [string]::IsNullOrWhiteSpace($explicitCoreFingerprint)) {
        $explicitCoreFingerprint.ToUpperInvariant()
    } elseif (-not [string]::IsNullOrWhiteSpace($localCoreFingerprint)) {
        $localCoreFingerprint
    } elseif ($preStatus -and -not [string]::IsNullOrWhiteSpace([string]$preStatus.coreFingerprint)) {
        [string]$preStatus.coreFingerprint
    } else { "" }
    $connectionSessionId = Get-ConnectionSessionId (Get-Value $values "--connection-session-id")
    $correlationId = Get-Value $values "--correlation-id" ("correlation-" + [Guid]::NewGuid().ToString("N"))
    $participantId = Get-Value $values "--participant-id" ("participant-" + [Guid]::NewGuid().ToString("N"))
    $operationId = Get-Value $values "--operation-id" ("operation-" + [Guid]::NewGuid().ToString("N"))
    $requiredAdapterFingerprint = Get-Value $values "--required-adapter-fingerprint" (Get-Value $values "--adapter-fingerprint")
    $artifactFingerprint = Get-Value $values "--artifact-fingerprint"
    $loadedAssemblyFingerprint = Get-Value $values "--loaded-assembly-fingerprint"
    $compatibilityKey = Get-Value $values "--compatibility-key"
    if ([string]::IsNullOrWhiteSpace($compatibilityKey)) {
        $modSetFingerprint = Get-Value $values "--mod-set-fingerprint" "unknown-mod-set"
        $modLoadOrderFingerprint = Get-Value $values "--mod-load-order-fingerprint" $profile.modConfiguration
        $configurationFingerprint = Get-BridgeTextSha256 (($profile.modConfiguration, $profile.arguments) -join "|")
        $sourceBuildIdentity = Get-Value $values "--source-build-identity" $requiredCoreFingerprint
        $rimWorldVersion = Get-Value $values "--rimworld-version" "unknown-rimworld-version"
        $saveTarget = Get-Value $values "--save-target" $savePolicy
        $mapTarget = Get-Value $values "--map-target" "unknown-map"
        $canonical = @(
            "operationKind=Restart", "desiredState=$targetPostcondition",
            "managedProfile=managed-test", "rimWorldVersion=$rimWorldVersion",
            "modSetFingerprint=$modSetFingerprint", "modLoadOrderFingerprint=$modLoadOrderFingerprint",
            "sourceBuildIdentity=$sourceBuildIdentity", "deploymentSlot=$runtimeSlotId",
            "expectedCoreFingerprint=$requiredCoreFingerprint", "expectedAdapterFingerprint=$requiredAdapterFingerprint",
            "configurationFingerprint=$configurationFingerprint", "userRootFingerprint=$userRootFingerprint",
            "saveTarget=$saveTarget", "mapTarget=$mapTarget",
            "requiresProcessReplacement=$requiresNewProcess",
            "lifecycleGeneration=$([string](Get-Value $values "--requested-lifecycle-generation" "0"))",
            "mutationScope=restart") -join "|"
        $compatibilityKey = Get-BridgeTextSha256 $canonical
    }
    $requestValues = @(
        "--bridge-root", $bridgeRoot, "--user-root", $userRoot,
        "--agent-id", $agent.value, "--package-id", (Get-Value $values "--package-id" "Lan.RimWorldDevBridge"),
        "--client-instance-id", $clientInstance.value, "--connection-session-id", $connectionSessionId,
        "--correlation-id", $correlationId, "--participant-id", $participantId,
        "--client-credential", $clientCredential,
        "--operation-id", $operationId, "--operation-kind", "Restart",
        "--desired-state", $targetPostcondition, "--compatibility-key", $compatibilityKey,
        "--runtime-slot-id", $runtimeSlotId,
        "--readiness", $readiness, "--save-policy", $savePolicy,
        "--reason", (Get-Value $values "--reason" "runtime-verification"),
        "--timeout-ms", (Get-Value $values "--timeout-ms" "120000"),
        "--max-launch-attempts", (Get-Value $values "--max-launch-attempts" "2"),
        "--launch-backoff-ms", (Get-Value $values "--launch-backoff-ms" "500")
    )
    if (-not [string]::IsNullOrWhiteSpace($requiredCoreFingerprint)) {
        $requestValues += @("--required-core-fingerprint", $requiredCoreFingerprint)
    }
    if (-not [string]::IsNullOrWhiteSpace($requiredAdapterFingerprint)) {
        $requestValues += @("--required-adapter-fingerprint", $requiredAdapterFingerprint)
    }
    if (-not [string]::IsNullOrWhiteSpace($artifactFingerprint)) {
        $requestValues += @("--artifact-fingerprint", $artifactFingerprint)
    }
    $deploymentId = Get-Value $values "--deployment-id"
    if (-not [string]::IsNullOrWhiteSpace($deploymentId)) {
        $requestValues += @("--deployment-id", $deploymentId)
    }
    if (-not [string]::IsNullOrWhiteSpace($loadedAssemblyFingerprint)) {
        $requestValues += @("--loaded-assembly-fingerprint", $loadedAssemblyFingerprint)
    }
    $requestValues += @("--target-postcondition", $targetPostcondition, "--allow-supersede")
    if ($requiresNewProcess) { $requestValues += "--requires-new-process" }
    if ($ownedProcessId -gt 0) { $requestValues += @("--requested-pid", [string]$ownedProcessId) }
    if ($preStatus -and $preStatus.available) {
        if (-not [string]::IsNullOrWhiteSpace([string]$preStatus.session)) {
            $requestValues += @("--requested-session-id", [string]$preStatus.session)
        }
        if ([long]$preStatus.lifecycleGeneration -gt 0) {
            $requestValues += @("--requested-lifecycle-generation", [string]$preStatus.lifecycleGeneration)
        }
    }
    if (Has-Flag $values "--keep-running") { $requestValues += "--keep-running" }
    $ensureValues = @($launchValues)
    for ($requestIndex = 0; $requestIndex -lt $requestValues.Count; $requestIndex++) {
        $requestValue = $requestValues[$requestIndex]
        if ($requestValue -in @("--bridge-root", "--user-root", "--agent-id")) {
            $requestIndex++
            continue
        }
        $ensureValues += $requestValue
        if ($requestValue -like "--*" -and $requestIndex + 1 -lt $requestValues.Count -and
            -not $requestValues[$requestIndex + 1].StartsWith("--")) {
            $requestIndex++
            $ensureValues += $requestValues[$requestIndex]
        }
    }
    $ensure = Invoke-Coordinator "ensure" $ensureValues
    if (-not $ensure.ok) {
        Clear-LeaseState $userRoot $agent.value $clientInstance.value
        $script:ExitCode = 4
        return New-RestartEnsureResult "FAILED" $false $false "none" $ensure.phase $null "retry restart ensure within the bounded managed-launch policy" $ensure ([string]$ensure.error)
    }
    $ticket = $ensure.ticket
    $waitValues = @($requestValues + @("--ticket", $ticket))
    $progressInterval = Get-ActivationProgressIntervalMs $values $config
    $wakeState = @{ last = [DateTime]::MinValue }
    $activationProgressCallback = $ProgressCallback
    $waitProgress = {
        param($phase, $details)
        if ([string]$phase -eq "WAITING_FOR_BRIDGE" -and
            ([DateTime]::UtcNow - $wakeState.last).TotalMilliseconds -ge 1000) {
            $wakeState.last = [DateTime]::UtcNow
            $wakeValues = @("--bridge-root", $bridgeRoot, "--user-root", $userRoot, "--timeout-ms", "1000")
            $previousExit = $script:ExitCode
            Invoke-Wake $wakeValues | Out-Null
            $script:ExitCode = $previousExit
        }
        if ($activationProgressCallback) { & $activationProgressCallback $phase $details }
    }
    $wait = Invoke-Coordinator "wait" $waitValues $true $waitProgress $progressInterval
    $result = New-RestartEnsureResult ([string]$wait.phase) $true ($wait.phase -eq "READY") "coordinator-owned" $wait.phase $null "restart status --ticket $ticket" $wait ([string]$wait.error)
    $result.ticket = $ticket
    $result.readiness = $readiness
    $result.contextHandshake = if ($wait.contextHandshake) { $wait.contextHandshake } else { $null }
    if ($wait.phase -ne "READY") {
        Clear-LeaseState $userRoot $agent.value $clientInstance.value
        $script:ExitCode = 4
    }
    return $result
}

function New-ActivationResult([bool] $ready, [string] $reason, [string] $phase,
    [bool] $coalesced, [int64] $elapsedMs, $details = $null) {
    if ($ready) { $reason = "bridge_ready"; $phase = "READY" }
    $result = [ordered]@{
        ready = $ready
        state = if ($ready) { "ready" } elseif ($reason -eq "activation_timeout") { "activation_in_progress" } else { "failed" }
        reason = $reason
        phase = $phase
        coalesced = $coalesced
        elapsedMs = $elapsedMs
        details = $details
    }
    if ($ready) {
        $result.legacyReason = "activation_ready"
        $result.legacyPhase = "BRIDGE_READY"
    }
    return Set-ActivationRecoveryFields $result
}

function Get-ActivationFailureReason($ensure) {
    $text = @(
        [string]$ensure.reason, [string]$ensure.status, [string]$ensure.phase,
        [string]$ensure.error, [string]$ensure.operatorAction,
        [string]$ensure.detail, [string]$ensure.details.error, [string]$ensure.details.reason,
        [string]$ensure.details.detail
    ) -join " "
    if ($text -match "sandbox_authorization|SANDBOX_AUTHORIZATION") { return "sandbox_authorization_missing" }
    if ($text -match "managed_test_(executable|working|user|mod)|profile_missing|profile.*required") { return "managed_profile_missing" }
    if ($text -match "launch_profile_invalid|validated_launch_profile") { return "launch_profile_invalid" }
    if ($text -match "missing_build_tooling|runtime_tools|coordinator_.*missing") { return "runtime_build_required" }
    if ($text -match "disk_runtime_mismatch|core_fingerprint_mismatch|deployment_mismatch|fingerprint") { return "deployment_mismatch" }
    if ($text -match "attached_live_process_requires_operator") { return "attached_live_process_requires_operator" }
    if ($text -match "attached_process|USER_RESTART_REQUIRED|manually attached") { return "attached_live_process_requires_operator" }
    if ($text -match "managed_launch_failed") { return "managed_launch_failed" }
    if ($text -match "managed_process_exited_before_ready|game_process_exited|process_exited|game.*exited") { return "managed_process_exited_before_ready" }
    if ($text -match "bridge_handshake_timeout") { return "bridge_handshake_timeout" }
    if ($text -match "wait_timeout|timeout") { return "activation_timeout" }
    return "bridge_load_failed"
}

function Wait-ForActivationState([string] $userRoot, [DateTime] $deadline, [int] $progressIntervalMs) {
    $lastProgress = [DateTime]::MinValue
    while ([DateTime]::UtcNow -lt $deadline) {
        $state = Read-ActivationState $userRoot
        if ($state -and [string]$state.state -in @("ready", "failed")) { return $state }
        if ([DateTime]::UtcNow.Subtract($lastProgress).TotalMilliseconds -ge $progressIntervalMs) {
            $elapsed = if ($state -and $state.startedUtc) {
                [int64]([DateTime]::UtcNow - [DateTimeOffset]::Parse([string]$state.startedUtc).UtcDateTime).TotalMilliseconds
            } else { 0 }
            $phase = if ($state) { [string]$state.phase } else { "starting" }
            Write-ActivationProgress "activation_in_progress" $phase $elapsed $null $true
            $lastProgress = [DateTime]::UtcNow
        }
        Start-Sleep -Milliseconds ([Math]::Min(250, $progressIntervalMs))
    }
    return $null
}

function Invoke-ActivationRecovery([string[]] $values, [string] $initialReason) {
    $config = Get-ClientConfig
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    if ($null -eq $bridgeRoot -or $null -eq $userRoot) {
        return New-ActivationResult $false "managed_profile_missing" "CONFIGURATION" $false 0
    }
    $timeoutMs = Get-ActivationTimeoutMs $values $config
    $progressIntervalMs = Get-ActivationProgressIntervalMs $values $config
    $started = [DateTime]::UtcNow
    $deadline = $started.AddMilliseconds($timeoutMs)
    $operationId = "activation-" + [Guid]::NewGuid().ToString("N")
    $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
    $clientInstance = Get-ClientInstanceId (Get-Value $values "--client-instance-id") $config
    $lock = $null
    # Activation invalidates only this client instance's prior lease.
    Clear-LeaseState $userRoot $agent.value $clientInstance.value
    while ([DateTime]::UtcNow -lt $deadline) {
        $lock = Try-OpenActivationLock $userRoot
        if ($lock) { break }
        $state = Read-ActivationState $userRoot
        if ($state -and $state.state -eq "in_progress") {
            $completed = Wait-ForActivationState $userRoot $deadline $progressIntervalMs
            if ($completed) {
                $elapsed = [int64]([DateTime]::UtcNow - $started).TotalMilliseconds
                $coalescedResult = if ($completed.result) { $completed.result } else {
                    New-ActivationResult ($completed.state -eq "ready") ([string]$completed.reason) ([string]$completed.phase) $true $elapsed
                }
                $coalescedResult.coalesced = $true
                return $coalescedResult
            }
            return New-ActivationResult $false "activation_timeout" "activation_in_progress" $true ([int64]([DateTime]::UtcNow - $started).TotalMilliseconds)
        }
        Start-Sleep -Milliseconds ([Math]::Min(100, $progressIntervalMs))
    }
    if (-not $lock) {
        return New-ActivationResult $false "activation_timeout" "activation_in_progress" $true ([int64]([DateTime]::UtcNow - $started).TotalMilliseconds)
    }
    try {
        Write-ActivationState $userRoot "in_progress" "waking" $initialReason $operationId $started.ToString("o") | Out-Null
        Write-ActivationProgress "in_progress" "waking" 0 $initialReason $false
        $wakeValues = @("--bridge-root", $bridgeRoot, "--user-root", $userRoot,
            "--timeout-ms", [string]([Math]::Min(5000, $timeoutMs)))
        $previousExit = $script:ExitCode
        $wake = Invoke-Wake $wakeValues
        $script:ExitCode = $previousExit
        if ($wake.available) {
            $elapsed = [int64]([DateTime]::UtcNow - $started).TotalMilliseconds
            $ready = New-ActivationResult $true "bridge_ready" "READY" $false $elapsed $wake
            Write-ActivationState $userRoot "ready" $ready.phase $ready.reason $operationId $started.ToString("o") $ready | Out-Null
            return $ready
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            $timeout = New-ActivationResult $false "activation_timeout" "waking" $false ([int64]([DateTime]::UtcNow - $started).TotalMilliseconds) $wake
            Write-ActivationState $userRoot "failed" "waking" $timeout.reason $operationId $started.ToString("o") $timeout | Out-Null
            return $timeout
        }

        Write-ActivationState $userRoot "in_progress" "starting" $wake.reason $operationId $started.ToString("o") | Out-Null
        Write-ActivationProgress "in_progress" "starting" ([int64]([DateTime]::UtcNow - $started).TotalMilliseconds) ([string]$wake.reason) $false
        $ensureValues = New-ActivationEnsureValues $values $bridgeRoot $userRoot $timeoutMs
        $progressCallback = {
            param($phase, $details)
            $elapsed = [int64]([DateTime]::UtcNow - $started).TotalMilliseconds
            Write-ActivationState $userRoot "in_progress" ([string]$phase) ([string]$details.error) $operationId $started.ToString("o") | Out-Null
            Write-ActivationProgress "in_progress" ([string]$phase) $elapsed ([string]$details.error) $false
        }
        $ensure = Invoke-RestartEnsure $ensureValues $progressCallback
        $elapsed = [int64]([DateTime]::UtcNow - $started).TotalMilliseconds
        if ([string]$ensure.status -eq "READY") {
            Write-ActivationProgress "in_progress" "refreshing_context" $elapsed $null $false
            $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
            $freshStatus = Get-BridgeStatus $bridgeRoot $userRoot $agent.value $config
            if ($freshStatus.available) {
                $ready = New-ActivationResult $true "bridge_ready" "READY" $false $elapsed $ensure
                $ready.status = $freshStatus
                Write-ActivationState $userRoot "ready" $ready.phase $ready.reason $operationId $started.ToString("o") $ready | Out-Null
                return $ready
            }
            $failure = New-ActivationResult $false "bridge_load_failed" "REFRESH" $false $elapsed $freshStatus
        } else {
            $failureReason = Get-ActivationFailureReason $ensure
            if ($initialReason -in @("disk_runtime_mismatch", "core_fingerprint_mismatch") -and
                $failureReason -in @("managed_profile_missing", "sandbox_authorization_missing", "bridge_load_failed")) {
                $failureReason = "deployment_mismatch"
            }
            $failure = New-ActivationResult $false $failureReason ([string]$ensure.phase) $false $elapsed $ensure
            $failure.initialReason = $initialReason
        }
        Write-ActivationState $userRoot "failed" $failure.phase $failure.reason $operationId $started.ToString("o") $failure | Out-Null
        return $failure
    }
    catch {
        $elapsed = [int64]([DateTime]::UtcNow - $started).TotalMilliseconds
        $failure = New-ActivationResult $false (Get-ActivationFailureReason ([ordered]@{ reason = $_.Exception.Message })) "FAILED" $false $elapsed $_.Exception.Message
        Write-ActivationState $userRoot "failed" "FAILED" $failure.reason $operationId $started.ToString("o") $failure | Out-Null
        return $failure
    }
    finally {
        if ($lock) { $lock.Dispose() }
    }
}

function Get-GoalRoot([string] $userRoot) {
    $root = Join-Path $userRoot "RimWorld-DevBridge-Goals"
    [IO.Directory]::CreateDirectory($root) | Out-Null
    return $root
}

function Test-GoalId([string] $value) {
    return -not [string]::IsNullOrWhiteSpace($value) -and $value.Length -le 128 -and
        $value -match '^[A-Za-z0-9._:-]+$'
}

function Get-GoalOperationPath([string] $userRoot, [string] $goalId) {
    return Join-Path (Get-GoalRoot $userRoot) ("goal-" + $goalId + ".json")
}

function Get-GoalLockPath([string] $userRoot, [string] $goalId) {
    return Join-Path (Get-GoalRoot $userRoot) ("goal-" + $goalId + ".lock")
}

function Get-GoalDriverLockPath([string] $userRoot, [string] $goalId) {
    return Join-Path (Get-GoalRoot $userRoot) ("goal-" + $goalId + ".driver.lock")
}

function Try-OpenGoalLock([string] $path) {
    try { return [IO.FileStream]::new($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] { return $null }
    catch [UnauthorizedAccessException] { return $null }
}

function Set-GoalProperty($state, [string] $name, $value) {
    if ($state.PSObject.Properties[$name]) { $state.$name = $value }
    else { $state | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

function Read-GoalOperation([string] $userRoot, [string] $goalId) {
    try { return Read-JsonFile (Get-GoalOperationPath $userRoot $goalId) }
    catch { return $null }
}

function Write-GoalOperation([string] $userRoot, $state) {
    Set-GoalProperty $state "updatedUtc" ([DateTime]::UtcNow.ToString("o"))
    Write-JsonAtomic (Get-GoalOperationPath $userRoot ([string]$state.goalId)) $state
    return $state
}

function New-GoalOperation([string] $goalId, [string] $desiredState, [string] $packageId,
    [int] $timeoutMs, [int] $noProgressTimeoutMs, [bool] $keepRunning,
    [string] $agentId, [string] $clientInstanceId, [string] $participantId) {
    $now = [DateTime]::UtcNow
    return [ordered]@{
        schema = 1
        operationKind = "runtime_goal"
        goalId = $goalId
        operationId = "goal-" + [Guid]::NewGuid().ToString("N")
        agentId = $agentId
        clientInstanceId = $clientInstanceId
        participantId = $participantId
        packageId = $packageId
        desiredState = $desiredState
        operationState = "queued"
        phase = "QUEUED"
        code = "goal_queued"
        startedUtc = $now.ToString("o")
        updatedUtc = $now.ToString("o")
        overallDeadlineUtc = $now.AddMilliseconds($timeoutMs).ToString("o")
        timeoutMs = $timeoutMs
        noProgressTimeoutMs = $noProgressTimeoutMs
        lastProgressUtc = $now.ToString("o")
        progressSequence = 0
        keepRunning = $keepRunning
        recoverable = $true
        waitFor = if ($desiredState -eq "bridge") { "bridge" } elseif ($desiredState -eq "map") { "map" } else { "game" }
        requiredAction = "activate authorized managed-test instance"
        retrySafe = $true
        operatorActionRequired = $false
        nextAction = "goal wait --goal-id $goalId"
        resourcesReleased = $false
        contextFresh = $false
        cancellationRequested = $false
        evidence = @()
    }
}

function Test-GoalOwner($state, [string] $agentId, [string] $clientInstanceId, [string] $participantId) {
    return $null -ne $state -and [string]$state.agentId -eq $agentId -and
        [string]$state.clientInstanceId -eq $clientInstanceId -and
        [string]$state.participantId -eq $participantId
}

function Get-GoalResponse($state, [string] $correlationId = $null) {
    if ($null -eq $state) { return New-ErrorResult "goal_not_found" "The durable goal operation was not found." }
    $result = [ordered]@{}
    foreach ($property in @("ok", "code", "message", "correlationId", "goalId", "operationId", "agentId", "clientInstanceId", "participantId", "operationState", "phase", "desiredState", "packageId", "startedUtc", "updatedUtc", "progressSequence", "pid", "sessionId", "lifecycleGeneration", "coreFingerprint", "cycleId", "ticket", "contextFresh", "recoverable", "requiredAction", "waitFor", "keepRunning", "retrySafe", "operatorActionRequired", "nextAction", "resourcesReleased", "evidence", "details")) {
        if ($state -is [System.Collections.IDictionary] -and $state.Contains($property)) { $result[$property] = $state[$property] }
        elseif ($state.PSObject.Properties[$property]) { $result[$property] = $state.$property }
    }
    if (-not $result.Contains("ok")) { $result.ok = $state.operationState -eq "succeeded" }
    if (-not $result.Contains("code")) { $result.code = if ($state.operationState -eq "succeeded") { "goal_ready" } else { "goal_in_progress" } }
    if (-not $result.Contains("correlationId")) { $result.correlationId = $correlationId }
    if ([string]$state.operationState -eq "succeeded") {
        $result.ok = $true
        $result.code = if ([string]$state.desiredState -eq "map") { "map_ready" } elseif ([string]$state.desiredState -eq "test_ready") { "test_ready" } else { "bridge_ready" }
        $result.phase = if ([string]$state.desiredState -eq "map") { "MAP_READY" } elseif ([string]$state.desiredState -eq "test_ready") { "TEST_READY" } else { "READY" }
        $result.recoverable = $false
        $result.requiredAction = "none"
        $result.waitFor = "none"
        $result.retrySafe = $true
        $result.operatorActionRequired = $false
        $result.nextAction = "none"
    }
    return $result
}

function Get-GoalResultField($result, [string] $name) {
    if ($null -eq $result) { return $null }
    if ($result.PSObject.Properties[$name]) { return $result.$name }
    foreach ($field in @($result.data)) {
        if ($field.PSObject.Properties["name"] -and [string]$field.name -eq $name) { return [string]$field.value }
    }
    return $null
}

function Write-GoalDriverState([string] $userRoot, $state) {
    $lock = $null
    for ($attempt = 0; $attempt -lt 8 -and $null -eq $lock; $attempt++) {
        $lock = Try-OpenGoalLock (Get-GoalLockPath $userRoot ([string]$state.goalId))
        if ($null -eq $lock) { Start-Sleep -Milliseconds 25 }
    }
    if ($null -eq $lock) { return $null }
    try {
        $current = Read-GoalOperation $userRoot ([string]$state.goalId)
        if ($current -and [string]$current.operationId -eq [string]$state.operationId -and
            [string]$current.operationState -in @("cancelled", "checkpointed")) {
            return $null
        }
        return Write-GoalOperation $userRoot $state
    }
    finally { $lock.Dispose() }
}

function Get-GoalDeadline([string] $value, [int] $fallbackMs) {
    try { return [DateTime]::Parse($value).ToUniversalTime() }
    catch { return [DateTime]::UtcNow.AddMilliseconds($fallbackMs) }
}

function Wait-GoalOperation([string] $userRoot, [string] $goalId, [int] $timeoutMs, [string] $correlationId,
    [string] $agentId, [string] $clientInstanceId, [string] $participantId) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $state = Read-GoalOperation $userRoot $goalId
        if ($state -and -not (Test-GoalOwner $state $agentId $clientInstanceId $participantId)) {
            $ownerError = New-ErrorResult "goal_owner_mismatch" "The goal belongs to a different client participant."
            $ownerError.correlationId = $correlationId
            return $ownerError
        }
        if ($state -and [string]$state.operationState -in @("succeeded", "failed", "cancelled", "checkpointed")) {
            return Get-GoalResponse $state $correlationId
        }
        Start-Sleep -Milliseconds 100
    }
    $state = Read-GoalOperation $userRoot $goalId
    if ($state -and -not (Test-GoalOwner $state $agentId $clientInstanceId $participantId)) {
        $ownerError = New-ErrorResult "goal_owner_mismatch" "The goal belongs to a different client participant."
        $ownerError.correlationId = $correlationId
        return $ownerError
    }
    $result = Get-GoalResponse $state $correlationId
    $result.ok = $false
    $result.code = "goal_wait_timeout"
    $result.operationState = "running"
    $result.recoverable = $true
    $result.retrySafe = $true
    $result.nextAction = "goal wait --goal-id $goalId"
    return $result
}

function Invoke-GoalDriver([string[]] $values, [string] $bridgeRoot, [string] $userRoot,
    $state, $agent, $config) {
    $goalId = [string]$state.goalId
    $desired = [string]$state.desiredState
    $timeoutMs = 120000
    [int]::TryParse([string]$state.timeoutMs, [ref]$timeoutMs) | Out-Null
    if ($timeoutMs -lt 1000 -or $timeoutMs -gt 600000) { $timeoutMs = 120000 }
    $noProgressMs = 120000
    [int]::TryParse([string]$state.noProgressTimeoutMs, [ref]$noProgressMs) | Out-Null
    if ($noProgressMs -lt 1000 -or $noProgressMs -gt 600000) { $noProgressMs = 120000 }
    $deadline = Get-GoalDeadline ([string]$state.overallDeadlineUtc) $timeoutMs
    $lastProgress = Get-GoalDeadline ([string]$state.lastProgressUtc) 0
    $goalReadiness = if ($desired -eq "bridge") { "bridge" } else { "map" }
    $goalValues = @($values + @("--bridge-root", $bridgeRoot, "--user-root", $userRoot,
        "--agent-id", $agent.value, "--package-id", [string]$state.packageId,
        "--readiness", $goalReadiness, "--target-postcondition", $desired,
        "--allow-supersede", "--requires-new-process"))
    if (-not ($values | Where-Object { $_ -eq "--timeout-ms" -or $_.StartsWith("--timeout-ms=", [StringComparison]::OrdinalIgnoreCase) })) {
        $goalValues += @("--timeout-ms", [string]$timeoutMs)
    }
    Set-GoalProperty $state "operationState" "running"
    Set-GoalProperty $state "phase" "ACTIVATING"
    Set-GoalProperty $state "code" "goal_activation_in_progress"
    if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
    try {
        $activation = Invoke-ActivationRecovery $goalValues "goal_not_ready"
        $controlled = Read-GoalOperation $userRoot $goalId
        if ($controlled -and [string]$controlled.operationState -in @("cancelled", "checkpointed")) { return Get-GoalResponse $controlled }
        if (-not $activation.ready) {
            $script:ExitCode = if ([string]$activation.reason -in @("sandbox_authorization_missing", "managed_profile_missing", "launch_profile_invalid")) { 3 } else { 4 }
            Set-GoalProperty $state "operationState" "failed"
            Set-GoalProperty $state "phase" ([string]$activation.phase)
            Set-GoalProperty $state "code" ([string]$activation.reason)
            Set-GoalProperty $state "details" $activation.details
            Set-GoalProperty $state "nextAction" "goal resume --goal-id $goalId"
            if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
            return Get-GoalResponse $state
        }
        $lastSignature = ""
        do {
            $status = Get-BridgeStatus $bridgeRoot $userRoot $agent.value $config
            if (-not $status.available) {
                Set-GoalProperty $state "phase" "WAITING_FOR_BRIDGE"
                Set-GoalProperty $state "code" ([string]$status.reason)
                Set-GoalProperty $state "waitFor" "bridge"
                if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
            }
            else {
                Set-GoalProperty $state "pid" ([int]$status.processId)
                Set-GoalProperty $state "sessionId" ([string]$status.session)
                Set-GoalProperty $state "lifecycleGeneration" ([long]$status.lifecycleGeneration)
                Set-GoalProperty $state "coreFingerprint" ([string]$status.coreFingerprint)
                Set-GoalProperty $state "contextFresh" $false
                if ($desired -eq "bridge") {
                    Set-GoalProperty $state "operationState" "succeeded"
                    Set-GoalProperty $state "phase" "READY"
                    Set-GoalProperty $state "code" "bridge_ready"
                    Set-GoalProperty $state "waitFor" "none"
                    Set-GoalProperty $state "nextAction" "none"
                    Set-GoalProperty $state "contextFresh" $true
                    Set-GoalProperty $state "evidence" @([ordered]@{ phase = "READY"; processId = $status.processId; session = $status.session; lifecycleGeneration = $status.lifecycleGeneration; coreFingerprint = $status.coreFingerprint })
                    if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
                    return Get-GoalResponse $state
                }
                $context = Invoke-BridgeCommand "AGENT_CONTEXT" ("packageId=" + [string]$state.packageId) $goalValues
                $gameLoaded = [string](Get-GoalResultField $context "gameLoaded") -eq "true"
                $mapReady = [string](Get-GoalResultField $context "mapReady") -eq "true"
                $ready = $gameLoaded -and $mapReady
                $signature = "{0}|{1}|{2}|{3}|{4}" -f $status.processId, $status.session, $status.lifecycleGeneration, $gameLoaded, $mapReady
                if ($signature -ne $lastSignature) {
                    $lastSignature = $signature
                    $lastProgress = [DateTime]::UtcNow
                    Set-GoalProperty $state "progressSequence" ([int]$state.progressSequence + 1)
                    Set-GoalProperty $state "lastProgressUtc" $lastProgress.ToString("o")
                }
                if ($ready) {
                    Set-GoalProperty $state "contextFresh" $true
                    Set-GoalProperty $state "operationState" "succeeded"
                    Set-GoalProperty $state "phase" $(if ($desired -eq "test_ready") { "TEST_READY" } else { "MAP_READY" })
                    Set-GoalProperty $state "code" $(if ($desired -eq "test_ready") { "test_ready" } else { "map_ready" })
                    Set-GoalProperty $state "waitFor" "none"
                    Set-GoalProperty $state "nextAction" "none"
                    Set-GoalProperty $state "evidence" @([ordered]@{ phase = $state.phase; processId = $status.processId; session = $status.session; lifecycleGeneration = $status.lifecycleGeneration; gameLoaded = $gameLoaded; mapReady = $mapReady; coreFingerprint = $status.coreFingerprint })
                    if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
                    return Get-GoalResponse $state
                }
                Set-GoalProperty $state "phase" $(if ($gameLoaded) { "WAITING_FOR_MAP" } else { "WAITING_FOR_GAME" })
                Set-GoalProperty $state "code" "runtime_loading"
                Set-GoalProperty $state "waitFor" $(if ($gameLoaded) { "map" } else { "game" })
                Set-GoalProperty $state "contextFresh" $true
                if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
            }
            if ([DateTime]::UtcNow - $lastProgress -gt [TimeSpan]::FromMilliseconds($noProgressMs)) {
                Set-GoalProperty $state "operationState" "failed"
                Set-GoalProperty $state "phase" "FAILED"
                Set-GoalProperty $state "code" "runtime_progress_timeout"
                Set-GoalProperty $state "details" "No lifecycle or readiness progress within the bounded watchdog window."
                Set-GoalProperty $state "nextAction" "goal resume --goal-id $goalId"
                if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
                return Get-GoalResponse $state
            }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        Set-GoalProperty $state "operationState" "failed"
        $script:ExitCode = 4
        Set-GoalProperty $state "phase" "FAILED"
        Set-GoalProperty $state "code" "goal_timeout"
        Set-GoalProperty $state "details" "The requested runtime postcondition was not reached before the bounded deadline."
        Set-GoalProperty $state "nextAction" "goal resume --goal-id $goalId"
        if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
        return Get-GoalResponse $state
    }
    catch {
        Set-GoalProperty $state "operationState" "failed"
        $script:ExitCode = 4
        Set-GoalProperty $state "phase" "FAILED"
        Set-GoalProperty $state "code" "goal_failed"
        Set-GoalProperty $state "details" (Redact-Text $_.Exception.Message)
        Set-GoalProperty $state "nextAction" "goal resume --goal-id $goalId"
        if ($null -eq (Write-GoalDriverState $userRoot $state)) { return Get-GoalResponse (Read-GoalOperation $userRoot $goalId) }
        return Get-GoalResponse $state
    }
}

function Invoke-Goal([string[]] $values) {
    $config = Get-ClientConfig
    $userRoot = Resolve-UserRoot (Get-Value $values "--user-root") $config
    $bridgeRoot = Resolve-BridgeRoot (Get-Value $values "--bridge-root") $config
    $agent = Get-AgentId $userRoot (Get-Value $values "--agent-id")
    $clientInstance = Get-ClientInstanceId (Get-Value $values "--client-instance-id") $config
    if ($null -eq $userRoot -or $null -eq $bridgeRoot) { $script:ExitCode = 3; return New-ErrorResult "configuration_error" "bridge and user roots are required" }
    if ($values.Count -lt 1) { throw "goal_operation_required: use ensure, status, wait, cancel, checkpoint, or resume" }
    $operation = $values[0].ToLowerInvariant()
    $goalId = Get-Value $values "--goal-id"
    $participantId = Get-Value $values "--participant-id" ("participant-goal-" +
        (Get-BridgeTextSha256 $goalId).Substring(0, 16).ToLowerInvariant())
    if ([string]::IsNullOrWhiteSpace($goalId) -and $operation -ne "list") { throw "goal_id_required: use --goal-id" }
    if (-not [string]::IsNullOrWhiteSpace($goalId) -and -not (Test-GoalId $goalId)) { throw "goal_id_invalid" }
    if ($operation -eq "status" -or $operation -eq "get") {
        $statusState = Read-GoalOperation $userRoot $goalId
        if ($statusState -and -not (Test-GoalOwner $statusState $agent.value $clientInstance.value $participantId)) {
            $script:ExitCode = 4
            return New-ErrorResult "goal_owner_mismatch" "The goal belongs to a different client participant."
        }
        if ($statusState -and [string]$statusState.operationState -eq "failed") { $script:ExitCode = 3 }
        return Get-GoalResponse $statusState (Get-Value $values "--correlation-id")
    }
    if ($operation -eq "cancel" -or $operation -eq "checkpoint") {
        $lock = Try-OpenGoalLock (Get-GoalLockPath $userRoot $goalId)
        if (-not $lock) { $script:ExitCode = 4; return New-ErrorResult "goal_operation_busy" "The goal state is being updated; retry safely." }
        try {
            $state = Read-GoalOperation $userRoot $goalId
            if ($null -eq $state) { $script:ExitCode = 3; return New-ErrorResult "goal_not_found" "The goal operation was not found." }
            if (-not (Test-GoalOwner $state $agent.value $clientInstance.value $participantId)) {
                $script:ExitCode = 4
                return New-ErrorResult "goal_owner_mismatch" "The goal belongs to a different client participant."
            }
            Clear-LeaseState $userRoot $agent.value $clientInstance.value
            Set-GoalProperty $state "resourcesReleased" $true
            Set-GoalProperty $state "cancellationRequested" ($operation -eq "cancel")
            Set-GoalProperty $state "operationState" $(if ($operation -eq "cancel") { "cancelled" } else { "checkpointed" })
            Set-GoalProperty $state "phase" $(if ($operation -eq "cancel") { "CANCELLED" } else { "READY_AWAITING_HUMAN" })
            Set-GoalProperty $state "code" $(if ($operation -eq "cancel") { "goal_cancelled" } else { "goal_checkpointed" })
            Set-GoalProperty $state "nextAction" $(if ($operation -eq "cancel") { "none" } else { "goal resume --goal-id $goalId" })
            Write-GoalOperation $userRoot $state | Out-Null
            return Get-GoalResponse $state (Get-Value $values "--correlation-id")
        }
        finally { $lock.Dispose() }
    }
    if ($operation -eq "wait") { return Wait-GoalOperation $userRoot $goalId ([int](Get-Value $values "--timeout-ms" "120000")) (Get-Value $values "--correlation-id") $agent.value $clientInstance.value $participantId }
    if ($operation -notin @("ensure", "resume")) { throw "goal_operation_invalid: use ensure, status, wait, cancel, checkpoint, or resume" }
    $desiredOption = Get-Value $values "--desired-state"
    $desired = if ([string]::IsNullOrWhiteSpace([string]$desiredOption)) { "test_ready" } else { [string]$desiredOption }
    if ($desired -notin @("bridge", "map", "test_ready")) { throw "desired_state_invalid: use bridge, map, or test_ready" }
    $goalTimeoutMs = 120000
    [int]::TryParse((Get-Value $values "--timeout-ms" "120000"), [ref]$goalTimeoutMs) | Out-Null
    if ($goalTimeoutMs -lt 1000 -or $goalTimeoutMs -gt 600000) { throw "goal_timeout_invalid: use 1000..600000 ms" }
    $goalNoProgressMs = 120000
    [int]::TryParse((Get-Value $values "--no-progress-timeout-ms" "120000"), [ref]$goalNoProgressMs) | Out-Null
    if ($goalNoProgressMs -lt 1000 -or $goalNoProgressMs -gt 600000) { throw "goal_no_progress_timeout_invalid: use 1000..600000 ms" }
    $packageOption = Get-Value $values "--package-id"
    $packageId = if ([string]::IsNullOrWhiteSpace([string]$packageOption)) { "Lan.RimWorldDevBridge" } else { [string]$packageOption }
    $lock = Try-OpenGoalLock (Get-GoalLockPath $userRoot $goalId)
    if (-not $lock) { return Wait-GoalOperation $userRoot $goalId $goalTimeoutMs (Get-Value $values "--correlation-id") $agent.value $clientInstance.value $participantId }
    try {
        $state = Read-GoalOperation $userRoot $goalId
        $legacyUnownedResume = $state -and $operation -eq "resume" -and
            [string]::IsNullOrWhiteSpace([string]$state.agentId) -and
            [string]::IsNullOrWhiteSpace([string]$state.clientInstanceId) -and
            [string]::IsNullOrWhiteSpace([string]$state.participantId)
        if ($state -and -not $legacyUnownedResume -and
            -not (Test-GoalOwner $state $agent.value $clientInstance.value $participantId)) {
            return New-ErrorResult "goal_owner_mismatch" "The goal belongs to a different client participant."
        }
        if ($operation -eq "resume" -and $state -and [string]$state.operationState -eq "succeeded") {
            return Get-GoalResponse $state (Get-Value $values "--correlation-id")
        }
        if ($operation -eq "resume" -and $state) {
            if ($legacyUnownedResume) {
                Set-GoalProperty $state "agentId" $agent.value
                Set-GoalProperty $state "clientInstanceId" $clientInstance.value
                Set-GoalProperty $state "participantId" $participantId
            }
            if ([string]::IsNullOrWhiteSpace([string]$desiredOption)) { $desired = [string]$state.desiredState }
            if ([string]::IsNullOrWhiteSpace([string]$packageOption)) { $packageId = [string]$state.packageId }
        }
        if ($null -eq $state -or $operation -eq "resume") {
            if ($null -eq $state) {
                $state = New-GoalOperation $goalId $desired $packageId $goalTimeoutMs $goalNoProgressMs $true $agent.value $clientInstance.value $participantId
            }
            else {
                Set-GoalProperty $state "operationState" "queued"
                Set-GoalProperty $state "phase" "QUEUED"
                Set-GoalProperty $state "code" "goal_queued"
                Set-GoalProperty $state "desiredState" $desired
                Set-GoalProperty $state "timeoutMs" $goalTimeoutMs
                Set-GoalProperty $state "noProgressTimeoutMs" $goalNoProgressMs
                Set-GoalProperty $state "overallDeadlineUtc" ([DateTime]::UtcNow.AddMilliseconds($goalTimeoutMs).ToString("o"))
                Set-GoalProperty $state "lastProgressUtc" ([DateTime]::UtcNow.ToString("o"))
                Set-GoalProperty $state "resourcesReleased" $false
                Set-GoalProperty $state "cancellationRequested" $false
            }
            Write-GoalOperation $userRoot $state | Out-Null
        }
    }
    finally { $lock.Dispose() }
    if ($operation -eq "ensure" -and [string]$state.operationState -in @("succeeded", "failed", "cancelled", "checkpointed")) {
        return Get-GoalResponse $state (Get-Value $values "--correlation-id")
    }
    $driverLock = Try-OpenGoalLock (Get-GoalDriverLockPath $userRoot $goalId)
    if ($driverLock) {
        try { return Invoke-GoalDriver $values $bridgeRoot $userRoot $state $agent $config }
        finally { $driverLock.Dispose() }
    }
    return Wait-GoalOperation $userRoot $goalId $goalTimeoutMs (Get-Value $values "--correlation-id") $agent.value $clientInstance.value $participantId
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

function Invoke-CoordinatorWaitWithProgress([string[]] $values, [bool] $EnsureRuntimeTools,
    [scriptblock] $ProgressCallback, [int] $ProgressIntervalMs) {
    $timeoutMs = 120000
    [int]::TryParse((Get-Value $values "--timeout-ms" "120000"), [ref]$timeoutMs) | Out-Null
    if ($timeoutMs -lt 100 -or $timeoutMs -gt 600000) {
        return [ordered]@{ ok = $false; error = "timeout_ms_invalid"; phase = "FAILED"; ticket = Get-Value $values "--ticket" }
    }
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    $last = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $previousExit = $script:ExitCode
        $last = Invoke-Coordinator "status" $values $EnsureRuntimeTools
        $script:ExitCode = $previousExit
        if ($ProgressCallback) { & $ProgressCallback ([string]$last.phase) $last }
        if ([string]$last.phase -in @("READY", "FAILED", "USER_RESTART_REQUIRED")) { return $last }
        Start-Sleep -Milliseconds $ProgressIntervalMs
    }
    $finalValues = @()
    $skipTimeoutValue = $false
    for ($index = 0; $index -lt $values.Count; $index++) {
        if ($skipTimeoutValue) { $skipTimeoutValue = $false; continue }
        $value = [string]$values[$index]
        if ($value -eq "--timeout-ms") {
            $finalValues += @("--timeout-ms", "1000")
            $skipTimeoutValue = $true
        } elseif ($value.StartsWith("--timeout-ms=", [StringComparison]::OrdinalIgnoreCase)) {
            $finalValues += "--timeout-ms=1000"
        } else {
            $finalValues += $values[$index]
        }
    }
    $previousExit = $script:ExitCode
    $final = Invoke-Coordinator "status" $finalValues $EnsureRuntimeTools
    $script:ExitCode = $previousExit
    if ($ProgressCallback) { & $ProgressCallback ([string]$final.phase) $final }
    if ([string]$final.phase -in @("READY", "FAILED", "USER_RESTART_REQUIRED")) { return $final }
    return [ordered]@{
        ok = $false
        error = "coordinator_wait_timeout"
        phase = if ($final) { $final.phase } elseif ($last) { $last.phase } else { "WAITING_FOR_BRIDGE" }
        ticket = if ($final) { $final.ticket } elseif ($last) { $last.ticket } else { Get-Value $values "--ticket" }
        details = if ($final) { $final } else { $last }
    }
}

function Invoke-Coordinator([string] $operation, [string[]] $values, [bool] $EnsureRuntimeTools = $true,
    [scriptblock] $ProgressCallback = $null, [int] $ProgressIntervalMs = 0) {
    if ($operation -eq "wait" -and $ProgressCallback -and $ProgressIntervalMs -gt 0) {
        return Invoke-CoordinatorWaitWithProgress $values $EnsureRuntimeTools $ProgressCallback $ProgressIntervalMs
    }
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
    $scope = Get-Value $values "--runtime-slot-id"
    if ([string]::IsNullOrWhiteSpace([string]$scope)) { $scope = Get-Value $values "--managed-profile" "default" }
    $scopeHash = Get-BridgeTextSha256 ([string]$scope).ToLowerInvariant()
    $coordinatorRoot = Join-Path $userRoot ("RimWorld-DevBridge-Coordinator-" + $scopeHash.Substring(0, 24))
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
        $context = Invoke-BridgeCommand "AGENT_CONTEXT" ("packageId=" + $packageId) $values $false $null $null $false
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
            if (-not $result.available -and (Test-ActivationEligibleReason $result.reason)) {
                $activation = Invoke-ActivationRecovery $args $result.reason
                if ($activation.ready) {
                    $result = Wait-ForFreshBridgeStatus $bridgeRoot $userRoot $agent.value $config 5000
                    if ($result.available) {
                        $freshStatus = Invoke-BridgeCommand "STATUS" "" $args $false $null $null $false
                        if (-not (Test-ResponseOk $freshStatus)) {
                            $result = New-ErrorResult "bridge_load_failed" "Fresh STATUS did not complete after activation."
                            $result.activation = $activation
                            $result.status = $freshStatus
                            $result = Set-ActivationRecoveryFields $result
                        }
                        else {
                            $result.activationRecovered = $true
                            $result.activation = $activation
                        }
                    }
                    else {
                        $postActivationStatus = $result
                        $result = New-ErrorResult "bridge_load_failed" "Bridge status remained unavailable after activation."
                        $result.activation = $activation
                        $result.status = $postActivationStatus
                        $result = Set-ActivationRecoveryFields $result
                    }
                }
                else {
                    $result = New-ErrorResult $activation.reason "Runtime activation did not complete."
                    $result.activation = $activation
                    $result = Set-ActivationRecoveryFields $result
                }
            }
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
            $result = Invoke-BridgeCommand $command (Get-Value $args "--argument" "") $args $false (Get-Value $args "--idempotency-key") (Get-Value $args "--lease-token") $false
        }
        "mutate" {
            $command = Get-CallCommand $args
            if ([string]::IsNullOrWhiteSpace($command)) { throw "command_required: use mutate --command <name>" }
            $result = Invoke-BridgeCommand $command (Get-Value $args "--argument" "") $args $true (Get-Value $args "--idempotency-key") (Get-Value $args "--lease-token") $false
        }
        "cancel" {
            $requestId = Get-Value $args "--request-id" (Get-Value $args "--argument")
            if ([string]::IsNullOrWhiteSpace($requestId)) { throw "request_id_required: use --request-id" }
            $result = Invoke-BridgeCommand "CANCEL" $requestId $args $false $null $null $false
        }
        "lease" {
            if ($args.Count -lt 1) { throw "lease_operation_required: use acquire, inspect, renew, or release" }
            switch ($args[0].ToLowerInvariant()) {
                "acquire" { $result = Invoke-BridgeCommand "WRITE_LEASE" (Get-Value $args "--context" "sandbox") $args $true $null $null $false }
                "inspect" { $result = Invoke-BridgeCommand "STATUS" "" $args }
                "renew" { $result = Invoke-BridgeCommand "RENEW_WRITE_LEASE" "" $args $true $null (Get-Value $args "--lease-token") $false }
                "release" { $result = Invoke-BridgeCommand "REVOKE_WRITE_LEASE" "" $args $true $null (Get-Value $args "--lease-token") $false }
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
        "review" {
            $userRoot = Resolve-UserRoot (Get-Value $args "--user-root") $config
            if ($null -eq $userRoot) { throw "user_root_required: review state is scoped to an existing user root" }
            $agent = Get-AgentId $userRoot (Get-Value $args "--agent-id")
            $result = Invoke-Review $args $agent $userRoot
        }
        "goal" { $result = Invoke-Goal $args }
        "adapter" {
            if ($args.Count -lt 1) { throw "adapter_operation_required: use publish or reload" }
            if ($args[0] -eq "reload") { $result = Invoke-BridgeCommand "RELOAD_ADAPTERS" "" $args $true $null $null $false; break }
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
                        $result = New-ErrorResult "attached_live_process_requires_operator" "The configured RimWorld process is attached; no process was claimed or stopped."
                        $result = Set-ActivationRecoveryFields $result
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
