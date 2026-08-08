param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Release'),
    [string]$GameOutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '1.6/Assemblies'),
    [string]$CoordinatorOutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '1.6/Assemblies/RestartCoordinator/net472'),
    [switch]$Build,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
$modRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceProject = Join-Path $modRoot 'Source/RimWorldDevBridge/RimWorldDevBridge.csproj'
$coordinatorProject = Join-Path $modRoot 'DevTools/RestartCoordinator/RimWorldDevBridge.RestartCoordinator.csproj'
$gameOutputRoot = [IO.Path]::GetFullPath($GameOutputDirectory)
$coordinatorOutputRoot = [IO.Path]::GetFullPath($CoordinatorOutputDirectory)
$coreSource = Join-Path $gameOutputRoot 'RimWorldDevBridge.dll'
$coordinatorSource = Join-Path $coordinatorOutputRoot 'RimWorldDevBridge.RestartCoordinator.exe'
$licenseSource = Join-Path $modRoot 'LICENSE'
$manifestPath = Join-Path $modRoot 'BRIDGE_MANIFEST.txt'
$verifier = Join-Path $PSScriptRoot 'Test-RimWorldDevBridgePackage.ps1'
$stagingRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(),
    'RimWorldDevBridgeRelease-' + [Guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $stagingRoot 'artifact'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$zipPath = $null

$packageEntries = @(
    @{ Relative = 'About/About.xml'; Source = (Join-Path $modRoot 'About/About.xml') },
    @{ Relative = 'LoadFolders.xml'; Source = (Join-Path $modRoot 'LoadFolders.xml') },
    @{ Relative = 'BRIDGE_MANIFEST.txt'; Source = $manifestPath },
    @{ Relative = 'BRIDGE_HANDOFF.md'; Source = (Join-Path $modRoot 'BRIDGE_HANDOFF.md') },
    @{ Relative = 'AGENTS.md'; Source = (Join-Path $modRoot 'AGENTS.md') },
    @{ Relative = 'LICENSE'; Source = (Join-Path $modRoot 'LICENSE') },
    @{ Relative = '1.6/Assemblies/RimWorldDevBridge.dll'; Source = $coreSource },
    @{ Relative = 'RestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe'; Source = $coordinatorSource },
    @{ Relative = 'DevTools/devbridge.ps1'; Source = (Join-Path $PSScriptRoot 'devbridge.ps1') },
    @{ Relative = 'DevTools/Send-RimWorldBridge.ps1'; Source = (Join-Path $PSScriptRoot 'Send-RimWorldBridge.ps1') },
    @{ Relative = 'DevTools/DEVBRIDGE_AGENT.md'; Source = (Join-Path $PSScriptRoot 'DEVBRIDGE_AGENT.md') }
)

function Read-BridgeVersion([string]$Path) {
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0 -and $line.Substring(0, $separator) -eq 'bridge') {
            return $line.Substring($separator + 1)
        }
    }
    throw 'BRIDGE_MANIFEST has no bridge version.'
}

function Copy-PackageEntry([hashtable]$Entry) {
    $destination = Join-Path $artifactRoot ($Entry.Relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    $parent = [IO.Path]::GetDirectoryName($destination)
    if (-not [IO.Directory]::Exists($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    Copy-Item -LiteralPath $Entry.Source -Destination $destination -Force
}

function Write-ZipEntry([IO.Compression.ZipArchive]$Archive, [hashtable]$Entry) {
    $zipEntry = $Archive.CreateEntry($Entry.Relative, [IO.Compression.CompressionLevel]::Optimal)
    $zipEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $input = $null
    $output = $null
    try {
        $input = [IO.File]::OpenRead($Entry.Source)
        $output = $zipEntry.Open()
        $input.CopyTo($output)
    }
    finally {
        if ($output) { $output.Dispose() }
        if ($input) { $input.Dispose() }
    }
}

try {
    if (-not (Test-Path -LiteralPath $sourceProject)) { throw "Source project is missing: $sourceProject" }
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Bridge manifest is missing: $manifestPath" }
    foreach ($entry in $packageEntries | Where-Object {
            $_.Source -ne $coordinatorSource -and (-not $Build -or $_.Source -ne $coreSource) }) {
        if (-not (Test-Path -LiteralPath $entry.Source -PathType Leaf)) {
            throw "Required package source is missing: $($entry.Source)"
        }
    }
    if ($Build) {
        & dotnet build $sourceProject -c Release "-p:DevBridgeGameOutputRoot=$gameOutputRoot"
        if (-not $?) { throw 'Source build failed.' }
        & dotnet build $coordinatorProject -c Release "-p:DevBridgeCoordinatorOutputRoot=$coordinatorOutputRoot"
        if (-not $?) { throw 'Restart coordinator build failed.' }
    }
    foreach ($entry in $packageEntries) {
        if (-not (Test-Path -LiteralPath $entry.Source -PathType Leaf)) {
            throw "Required package source is missing: $($entry.Source)"
        }
    }
    if (-not (Test-Path -LiteralPath $coordinatorSource -PathType Leaf)) {
        throw "Required package source is missing: $coordinatorSource"
    }
    $bridgeVersion = Read-BridgeVersion $manifestPath

    foreach ($entry in $packageEntries) { Copy-PackageEntry $entry }

    & $verifier -ArtifactPath $artifactRoot -ExpectedBridgeVersion $bridgeVersion -ExpectedProtocol 10 `
        -ExpectedCorePath $coreSource -ExpectedLicensePath $licenseSource
    if (-not $?) { throw 'Staged package verification failed.' }

    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $zipPath = Join-Path $outputRoot ('RimWorldDevBridge-{0}.zip' -f $bridgeVersion)
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $packageEntries) { Write-ZipEntry $archive $entry }
    }
    finally { $archive.Dispose() }

    & $verifier -ArtifactPath $zipPath -ExpectedBridgeVersion $bridgeVersion -ExpectedProtocol 10 `
        -ExpectedCorePath $coreSource -ExpectedLicensePath $licenseSource
    if (-not $?) { throw 'Final ZIP verification failed.' }

    $size = (Get-Item -LiteralPath $zipPath).Length
    $sourceHash = (Get-FileHash -LiteralPath $coreSource -Algorithm SHA256).Hash
    Write-Output ('package=PASS artifact={0} bytes={1} entries={2}' -f $zipPath, $size, $packageEntries.Count)
    Write-Output ('coreBytes={0} coreSha256={1}' -f (Get-Item -LiteralPath $coreSource).Length, $sourceHash)
    Write-Output 'externalAdapters=0'
    foreach ($entry in $packageEntries) { Write-Output ('entry={0}' -f $entry.Relative) }
    if (-not $KeepStaging) { [IO.Directory]::Delete($stagingRoot, $true) }
}
catch {
    if (Test-Path -LiteralPath $stagingRoot) { [IO.Directory]::Delete($stagingRoot, $true) }
    throw
}
finally {
    if ($KeepStaging -and (Test-Path -LiteralPath $stagingRoot)) {
        Write-Output ('staging={0}' -f $stagingRoot)
    }
}
