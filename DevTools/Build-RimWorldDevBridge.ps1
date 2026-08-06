[CmdletBinding()]
param(
    [string]$RimWorldManagedDir,
    [string]$HarmonyPath,
    [string]$ConfigPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Build/devbridge.build.json'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Read-Config([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) }
    catch { throw "Build configuration is not valid JSON: $path. Copy Build/devbridge.build.json.example and edit only local, untracked values." }
}

function Config-Value($config, [string]$name) {
    if ($null -eq $config) { return $null }
    $property = $config.PSObject.Properties[$name]
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}

function First-Value([string]$explicit, [string]$environmentName, $config, [string]$configName) {
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return $explicit }
    $environment = [Environment]::GetEnvironmentVariable($environmentName)
    if (-not [string]::IsNullOrWhiteSpace($environment)) { return $environment }
    return (Config-Value $config $configName)
}

function Add-Candidate([Collections.Generic.List[string]]$list, [string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return }
    try { $full = [IO.Path]::GetFullPath($path) } catch { return }
    if (-not $list.Contains($full)) { $list.Add($full) }
}

function Find-ManagedDirectory {
    $candidates = New-Object 'Collections.Generic.List[string]'
    Add-Candidate $candidates ([Environment]::GetEnvironmentVariable('RIMWORLD_ROOT'))
    Add-Candidate $candidates ([Environment]::GetEnvironmentVariable('RIMWORLD_MANAGED_DIR'))
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles')
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $home = [Environment]::GetEnvironmentVariable('HOME')
    $userProfile = [Environment]::GetEnvironmentVariable('USERPROFILE')
    foreach ($steamRoot in @($programFiles, $programFilesX86, $home, $userProfile)) {
        if ([string]::IsNullOrWhiteSpace($steamRoot)) { continue }
        Add-Candidate $candidates (Join-Path $steamRoot 'Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed')
        Add-Candidate $candidates (Join-Path $steamRoot '.steam/steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed')
        Add-Candidate $candidates (Join-Path $steamRoot '.local/share/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed')
    }
    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'Assembly-CSharp.dll') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'UnityEngine.CoreModule.dll') -PathType Leaf)) {
            return $candidate
        }
    }
    throw 'RimWorld Managed assemblies were not found. Supply -RimWorldManagedDir, set RIMWORLD_MANAGED_DIR, or create an untracked Build/devbridge.build.json.'
}

function Find-Harmony([string]$managedDirectory) {
    $explicit = First-Value $HarmonyPath 'RIMWORLD_HARMONY_PATH' $config 'harmonyPath'
    if (-not [string]::IsNullOrWhiteSpace($explicit)) { return ([IO.Path]::GetFullPath($explicit)) }
    $managedCandidate = Join-Path $managedDirectory '0Harmony.dll'
    if (Test-Path -LiteralPath $managedCandidate -PathType Leaf) { return $managedCandidate }
    $roots = @(
        [Environment]::GetEnvironmentVariable('ProgramFiles'),
        [Environment]::GetEnvironmentVariable('ProgramFiles(x86)'),
        [Environment]::GetEnvironmentVariable('HOME'),
        [Environment]::GetEnvironmentVariable('USERPROFILE')
    )
    foreach ($steamRoot in $roots) {
        if ([string]::IsNullOrWhiteSpace($steamRoot)) { continue }
        $workshop = Join-Path $steamRoot 'Steam/steamapps/workshop/content/294100'
        if (-not (Test-Path -LiteralPath $workshop -PathType Container)) { continue }
        foreach ($directory in @(Get-ChildItem -LiteralPath $workshop -Directory -ErrorAction SilentlyContinue | Sort-Object FullName)) {
            $candidate = Join-Path $directory.FullName 'Current/Assemblies/0Harmony.dll'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw '0Harmony.dll was not found. Supply -HarmonyPath, set RIMWORLD_HARMONY_PATH, or add an untracked build configuration.'
}

function Assembly-Name([string]$path) {
    try { return [Reflection.AssemblyName]::GetAssemblyName($path).Name }
    catch { throw "Cannot read managed assembly identity from '$path': $($_.Exception.Message)" }
}

function Validate-Dependencies([string]$managedDirectory, [string]$harmony) {
    $required = @(
        @{ Path = (Join-Path $managedDirectory 'Assembly-CSharp.dll'); Name = 'Assembly-CSharp' },
        @{ Path = (Join-Path $managedDirectory 'UnityEngine.CoreModule.dll'); Name = 'UnityEngine.CoreModule' },
        @{ Path = (Join-Path $managedDirectory 'UnityEngine.IMGUIModule.dll'); Name = 'UnityEngine.IMGUIModule' },
        @{ Path = (Join-Path $managedDirectory 'UnityEngine.TextRenderingModule.dll'); Name = 'UnityEngine.TextRenderingModule' },
        @{ Path = (Join-Path $managedDirectory 'UnityEngine.ImageConversionModule.dll'); Name = 'UnityEngine.ImageConversionModule' },
        @{ Path = (Join-Path $managedDirectory 'UnityEngine.ScreenCaptureModule.dll'); Name = 'UnityEngine.ScreenCaptureModule' },
        @{ Path = $harmony; Name = '0Harmony' }
    )
    foreach ($item in $required) {
        if (-not (Test-Path -LiteralPath $item.Path -PathType Leaf)) { throw "Required dependency is missing: $($item.Path)" }
        $actual = Assembly-Name $item.Path
        if ($actual -ne $item.Name) { throw "Dependency identity mismatch at '$($item.Path)': expected $($item.Name), actual $actual" }
    }
    return $required
}

function Invoke-Build([string]$project, [string]$managedDirectory, [string]$harmony) {
    $arguments = @('build', $project, '-c', $Configuration,
        "/p:RimWorldManagedDir=$managedDirectory", "/p:RimWorldHarmonyPath=$harmony")
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $project with exit code $LASTEXITCODE." }
}

function Invoke-Script([string]$path, [string[]]$arguments) {
    & $path @arguments
    if (-not $?) { throw "Validation failed: $path." }
}

$config = Read-Config $ConfigPath
if ([string]::IsNullOrWhiteSpace($Configuration)) {
    $Configuration = Config-Value $config 'configuration'
    if ([string]::IsNullOrWhiteSpace($Configuration)) { $Configuration = 'Release' }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Config-Value $config 'outputDirectory'
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root 'Release' }
}

if ($PortableOnly) {
    Invoke-Script (Join-Path $PSScriptRoot 'Test-Portable.ps1') -arguments @()
    Write-Output 'portableBuild=PASS'
    exit 0
}

$managed = First-Value $RimWorldManagedDir 'RIMWORLD_MANAGED_DIR' $config 'rimWorldManagedDir'
if ([string]::IsNullOrWhiteSpace($managed)) { $managed = Find-ManagedDirectory }
$managed = [IO.Path]::GetFullPath($managed)
$harmony = Find-Harmony $managed
$harmony = [IO.Path]::GetFullPath($harmony)
Validate-Dependencies $managed $harmony | Out-Null

$coreProject = Join-Path $root 'Source/RimWorldDevBridge/RimWorldDevBridge.csproj'
$harnessProject = Join-Path $root 'DevTools/CompatibilityHarness/CompatibilityHarness.csproj'
$coordinatorProject = Join-Path $root 'DevTools/RestartCoordinator/RimWorldDevBridge.RestartCoordinator.csproj'
Invoke-Build $coreProject $managed $harmony
Invoke-Build $coordinatorProject $managed $harmony
Invoke-Build $harnessProject $managed $harmony

$harness = Join-Path $root ('DevTools/CompatibilityHarness/bin/{0}/net472/RimWorldDevBridge.CompatibilityHarness.exe' -f $Configuration)
if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) { throw "Compatibility harness output is missing: $harness" }
& $harness
if ($LASTEXITCODE -ne 0) { throw "Compatibility harness failed with exit code $LASTEXITCODE." }
Invoke-Script (Join-Path $PSScriptRoot 'Test-BridgeSourceInvariants.ps1') -arguments @()

$oldManaged = [Environment]::GetEnvironmentVariable('RIMWORLD_MANAGED_DIR', 'Process')
$oldHarmony = [Environment]::GetEnvironmentVariable('RIMWORLD_HARMONY_PATH', 'Process')
try {
    $env:RIMWORLD_MANAGED_DIR = $managed
    $env:RIMWORLD_HARMONY_PATH = $harmony
    & (Join-Path $PSScriptRoot 'Package-RimWorldDevBridge.ps1') -Build -OutputDirectory $OutputDirectory
    if (-not $?) { throw 'Package creation failed.' }
    & (Join-Path $PSScriptRoot 'Test-RimWorldDevBridgePackageSmoke.ps1') -ArtifactPath (Join-Path $OutputDirectory 'RimWorldDevBridge-2.2.0.zip')
    if (-not $?) { throw 'Package smoke validation failed.' }
}
finally {
    [Environment]::SetEnvironmentVariable('RIMWORLD_MANAGED_DIR', $oldManaged, 'Process')
    [Environment]::SetEnvironmentVariable('RIMWORLD_HARMONY_PATH', $oldHarmony, 'Process')
}
Write-Output ('fullBuild=PASS configuration={0} managedDir={1} harmony={2}' -f $Configuration, $managed, $harmony)
