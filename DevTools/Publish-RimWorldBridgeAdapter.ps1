param(
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [Parameter(Mandatory=$true)][string]$AdapterId,
    [Parameter(Mandatory=$true)][string]$DisplayName,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Generation,
    [Parameter(Mandatory=$true)][string]$ProviderType,
    [Parameter(Mandatory=$true)][string[]]$CommandSpecs,
    [string]$Destination = (Join-Path $PSScriptRoot "HotAdapters"),
    [string[]]$RequiredPackageIds = @(),
    [string[]]$OptionalPackageIds = @(),
    [string[]]$NoMapCommands = @(),
    [string[]]$UiOnlyCommands = @(),
    [string[]]$ReversibleCommands = @(),
    [string[]]$TemporaryCommands = @(),
    [string[]]$DestructiveCommands = @(),
    [string[]]$ExpensiveCommands = @(),
    [string[]]$SimulationCommands = @(),
    [hashtable]$ProviderCommandAliases = @{},
    [string]$ChangeSummary = "",
    [datetime]$BuildUtc = [DateTime]::UtcNow,
    [ValidateRange(1,2147483647)][int]$ProtocolMin = 10,
    [ValidateRange(1,2147483647)][int]$ProtocolMax = 10,
    [switch]$LoadedAssembly,
    [string]$LoadedPackageId = "",
    [string]$LoadedModulePath = ""
)

$ErrorActionPreference = "Stop"

function Test-Name([string]$Value) {
    return -not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[A-Za-z0-9_.-]+$'
}

function Contains-Name([string[]]$Values, [string]$Name) {
    return @($Values | Where-Object { $_ -ieq $Name }).Count -gt 0
}

function Publish-Atomic([string]$TemporaryPath, [string]$FinalPath) {
    if ([IO.File]::Exists($FinalPath)) {
        $backup = $FinalPath + ".previous"
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
        [IO.File]::Replace($TemporaryPath, $FinalPath, $backup)
        [IO.File]::Delete($backup)
    }
    else {
        [IO.File]::Move($TemporaryPath, $FinalPath)
    }
}

if (-not (Test-Name $AdapterId)) { throw "AdapterId is invalid." }
if (-not (Test-Name $Generation)) { throw "Generation is invalid." }
if ([string]::IsNullOrWhiteSpace($ProviderType)) { throw "ProviderType is required." }
if ($ProtocolMin -gt $ProtocolMax) { throw "ProtocolMin cannot exceed ProtocolMax." }
if ($LoadedAssembly -and ([string]::IsNullOrWhiteSpace($LoadedPackageId) -or
    [string]::IsNullOrWhiteSpace($LoadedModulePath))) {
    throw "LoadedPackageId and LoadedModulePath are required for a loaded assembly."
}

$source = [IO.Path]::GetFullPath($AssemblyPath)
if (-not [IO.File]::Exists($source)) { throw "Adapter assembly was not found: $source" }
$destinationRoot = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($destinationRoot) | Out-Null
$assemblyFile = [IO.Path]::GetFileName($source)
if (-not $assemblyFile.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Adapter assembly must have a .dll extension."
}
$publishedAssembly = if ($LoadedAssembly) { $source } else { [IO.Path]::Combine($destinationRoot, $assemblyFile) }

if (-not $LoadedAssembly -and -not $source.Equals($publishedAssembly, [StringComparison]::OrdinalIgnoreCase)) {
    $assemblyTemp = $publishedAssembly + "." + [Guid]::NewGuid().ToString("N") + ".tmp"
    [IO.File]::Copy($source, $assemblyTemp, $false)
    Publish-Atomic $assemblyTemp $publishedAssembly
}

$bytes = [IO.File]::ReadAllBytes($publishedAssembly)
$algorithm = [Security.Cryptography.SHA256]::Create()
try { $contentHash = [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace("-", "") }
finally { $algorithm.Dispose() }
$identity = [Reflection.AssemblyName]::GetAssemblyName($publishedAssembly).FullName
$moduleMvid = $null
if ($LoadedAssembly) {
    $reflectionAssembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($source)
    $moduleMvid = $reflectionAssembly.ManifestModule.ModuleVersionId.ToString("D")
}

$commands = @()
$names = @{}
foreach ($spec in $CommandSpecs) {
    $parts = "$spec" -split '\|', 3
    if ($parts.Count -ne 3) { throw "Invalid command spec: $spec" }
    $name = $parts[0].Trim().ToUpperInvariant()
    if (-not (Test-Name $name)) { throw "Invalid command name: $name" }
    if ($names.ContainsKey($name)) { throw "Duplicate command: $name" }
    $names[$name] = $true
    $legacyMode = $parts[1].Trim().ToUpperInvariant()
    if ($legacyMode -ne "R" -and $legacyMode -ne "W") { throw "Invalid command mode for $name." }
    $mode = if (Contains-Name $DestructiveCommands $name) { "PotentiallyDestructive" }
        elseif (Contains-Name $TemporaryCommands $name) { "TemporaryTestMutation" }
        elseif (Contains-Name $ReversibleCommands $name) { "Reversible" }
        elseif (Contains-Name $UiOnlyCommands $name) { "UiOnly" }
        elseif ($legacyMode -eq "W") { "PersistentMutation" }
        else { "PureRead" }
    $cost = if (Contains-Name $SimulationCommands $name) { "Simulation" }
        elseif (Contains-Name $ExpensiveCommands $name) { "Expensive" }
        else { "Normal" }
    $commands += [ordered]@{
        name = $name
        description = $parts[2].Trim()
        mode = $mode
        cost = $cost
        requiresMap = -not (Contains-Name $NoMapCommands $name)
        argumentSchema = "legacy:string"
        resultSchema = "legacy:lines"
        schemaVersion = 1
        minimumExecutionBudgetMs = 25
        providerCommand = $(if ($ProviderCommandAliases.ContainsKey($name)) {
            "$($ProviderCommandAliases[$name])".Trim().ToUpperInvariant()
        } else { $name })
    }
}
if ($commands.Count -eq 0) { throw "At least one command is required." }

$manifest = [ordered]@{
    manifestVersion = 2
    adapterId = $AdapterId
    displayName = $DisplayName
    version = $Version
    generation = $Generation
    buildUtc = $BuildUtc.ToUniversalTime().ToString("o")
    assemblyFile = $(if ($LoadedAssembly) { $null } else { $assemblyFile })
    assemblyIdentity = $identity
    assemblyBytes = $bytes.Length
    contentHash = $contentHash
    providerType = $ProviderType
    protocolMin = $ProtocolMin
    protocolMax = $ProtocolMax
    commands = $commands
    requiredPackageIds = @($RequiredPackageIds)
    optionalPackageIds = @($OptionalPackageIds)
    changeSummary = $ChangeSummary
    assemblySource = $(if ($LoadedAssembly) { "loaded" } else { "file" })
    modulePackageId = $(if ($LoadedAssembly) { $LoadedPackageId } else { $null })
    moduleRelativePath = $(if ($LoadedAssembly) { $LoadedModulePath } else { $null })
    moduleMvid = $moduleMvid
}

$manifestPath = [IO.Path]::Combine($destinationRoot, "$AdapterId.$Generation.manifest.json")
$manifestTemp = $manifestPath + "." + [Guid]::NewGuid().ToString("N") + ".tmp"
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestTemp, $json, [Text.UTF8Encoding]::new($false))
Publish-Atomic $manifestTemp $manifestPath

Write-Output "adapter=$publishedAssembly"
Write-Output "manifest=$manifestPath"
Write-Output "sha256=$contentHash"
Write-Output "reload=RELOAD_HOT_ADAPTERS"
