param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Release'),
    [switch]$Build,
    [switch]$KeepStaging
)

$ErrorActionPreference = "Stop"
$modRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceProject = Join-Path $modRoot 'Source/RimWorldDevBridge/RimWorldDevBridge.csproj'
$coreSource = Join-Path $modRoot '1.6/Assemblies/RimWorldDevBridge.dll'
$adapterSource = Join-Path $modRoot 'DevTools/HotAdapters'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$manifestPath = Join-Path $modRoot 'BRIDGE_MANIFEST.txt'
$verifier = Join-Path $PSScriptRoot 'Test-RimWorldDevBridgePackage.ps1'
$stagingRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(),
    'RimWorldDevBridgeRelease-' + [Guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $stagingRoot 'artifact'
$zipPath = $null
$supported = New-Object 'System.Collections.Generic.List[object]'
$excluded = New-Object 'System.Collections.Generic.List[object]'

function Parse-Manifest([string]$Path) {
    try {
        $manifest = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
        if ($null -eq $manifest) { throw 'manifest is empty' }
        if ([string]::IsNullOrWhiteSpace($manifest.adapterId)) { throw 'adapterId is required' }
        if ([string]::IsNullOrWhiteSpace($manifest.generation)) { throw 'generation is required' }
        $buildUtc = [DateTime]::MinValue
        if (-not [DateTime]::TryParse($manifest.buildUtc, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AdjustToUniversal, [ref]$buildUtc)) {
            throw 'buildUtc is invalid'
        }
        $manifest | Add-Member -NotePropertyName _Path -NotePropertyValue $Path
        $manifest | Add-Member -NotePropertyName _BuildUtc -NotePropertyValue $buildUtc
        return $manifest
    }
    catch { throw ("Cannot read adapter manifest {0}: {1}" -f $Path, $_.Exception.Message) }
}

function Test-Protocol([object]$Manifest) {
    return $Manifest.protocolMin -le 10 -and $Manifest.protocolMax -ge 10
}

function Test-FileAdapter([object]$Manifest) {
    if (-not [string]::IsNullOrWhiteSpace($Manifest.assemblySource) -and
        -not [string]::Equals($Manifest.assemblySource, 'file', [StringComparison]::OrdinalIgnoreCase)) {
        return 'assemblySource is not file'
    }
    if ([string]::IsNullOrWhiteSpace($Manifest.assemblyFile) -or
        [IO.Path]::GetFileName($Manifest.assemblyFile) -ne $Manifest.assemblyFile -or
        -not $Manifest.assemblyFile.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        return 'assemblyFile is unsafe or missing'
    }
    $dll = Join-Path $adapterSource $Manifest.assemblyFile
    if (-not (Test-Path -LiteralPath $dll)) { return 'assembly file is missing' }
    $info = Get-Item -LiteralPath $dll
    if ([int64]$Manifest.assemblyBytes -ne [int64]$info.Length) { return 'assemblyBytes mismatch' }
    $actualHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$Manifest.contentHash,
        [StringComparison]::OrdinalIgnoreCase)) { return 'contentHash mismatch' }
    $identity = [Reflection.AssemblyName]::GetAssemblyName($dll).FullName
    if ($identity -ne [string]$Manifest.assemblyIdentity) { return 'assemblyIdentity mismatch' }
    if ($null -eq $Manifest.commands -or @($Manifest.commands).Count -eq 0) { return 'command contract is missing' }
    return $null
}

try {
    if (-not (Test-Path -LiteralPath $sourceProject)) { throw "Source project is missing: $sourceProject" }
    if (-not (Test-Path -LiteralPath $adapterSource)) { throw "Adapter directory is missing: $adapterSource" }
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Bridge manifest is missing: $manifestPath" }
    if ($Build) {
        & dotnet build $sourceProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "Source build failed with exit code $LASTEXITCODE." }
    }
    if (-not (Test-Path -LiteralPath $coreSource)) { throw "Built core assembly is missing: $coreSource" }

    $bridgeValues = @{}
    foreach ($line in [IO.File]::ReadAllLines($manifestPath)) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) { $bridgeValues[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
    }
    $bridgeVersion = [string]$bridgeValues['bridge']
    if ([string]::IsNullOrWhiteSpace($bridgeVersion)) { throw 'BRIDGE_MANIFEST has no bridge version.' }

    $records = @([IO.Directory]::GetFiles($adapterSource, '*.manifest.json') |
        ForEach-Object { Parse-Manifest $_ })
    foreach ($group in ($records | Group-Object -Property adapterId)) {
        $compatible = @($group.Group | Where-Object { Test-Protocol $_ } |
            Sort-Object @{Expression={$_._BuildUtc}; Descending=$true},
                @{Expression={$_.generation}; Descending=$true})
        if ($compatible.Count -eq 0) {
            [void]$excluded.Add([PSCustomObject]@{ Adapter=$group.Name; Generation='none'; Reason='protocol incompatible' })
            continue
        }
        $candidate = $compatible[0]
        if (-not [string]::IsNullOrWhiteSpace($candidate.assemblySource) -and
            -not [string]::Equals($candidate.assemblySource, 'file', [StringComparison]::OrdinalIgnoreCase)) {
            [void]$excluded.Add([PSCustomObject]@{ Adapter=$group.Name; Generation=$candidate.generation; Reason='loaded-only adapter; no distributable DLL' })
            foreach ($older in $compatible | Select-Object -Skip 1) {
                [void]$excluded.Add([PSCustomObject]@{ Adapter=$group.Name; Generation=$older.generation; Reason='superseded by loaded-only generation' })
            }
            continue
        }
        $reason = Test-FileAdapter $candidate
        if ($null -ne $reason) {
            throw ("Current supported generation cannot be packaged without changing its manifest: {0} {1} ({2})" -f
                $candidate.adapterId, $candidate.generation, $reason)
        }
        [void]$supported.Add($candidate)
        foreach ($older in $compatible | Select-Object -Skip 1) {
            [void]$excluded.Add([PSCustomObject]@{ Adapter=$group.Name; Generation=$older.generation; Reason='superseded by newer supported generation' })
        }
        foreach ($incompatible in $records | Where-Object {
                $_.adapterId -ieq $group.Name -and -not (Test-Protocol $_) }) {
            [void]$excluded.Add([PSCustomObject]@{ Adapter=$group.Name; Generation=$incompatible.generation; Reason='protocol incompatible' })
        }
    }

    [IO.Directory]::CreateDirectory((Join-Path $artifactRoot 'About')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $artifactRoot '1.6/Assemblies')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $artifactRoot 'DevTools/HotAdapters')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $modRoot 'About/About.xml') -Destination (Join-Path $artifactRoot 'About/About.xml')
    Copy-Item -LiteralPath (Join-Path $modRoot 'LoadFolders.xml') -Destination (Join-Path $artifactRoot 'LoadFolders.xml')
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $artifactRoot 'BRIDGE_MANIFEST.txt')
    Copy-Item -LiteralPath $coreSource -Destination (Join-Path $artifactRoot '1.6/Assemblies/RimWorldDevBridge.dll')
    foreach ($record in $supported) {
        Copy-Item -LiteralPath $record._Path -Destination (Join-Path $artifactRoot 'DevTools/HotAdapters')
        Copy-Item -LiteralPath (Join-Path $adapterSource $record.assemblyFile) -Destination (Join-Path $artifactRoot 'DevTools/HotAdapters')
    }

    & $verifier -ArtifactPath $artifactRoot -ExpectedBridgeVersion $bridgeVersion -ExpectedProtocol 10
    if (-not $?) { throw 'Staged package verification failed.' }

    $outputFull = [IO.Path]::GetFullPath($outputRoot)
    [IO.Directory]::CreateDirectory($outputFull) | Out-Null
    $zipPath = Join-Path $outputFull ("RimWorldDevBridge-{0}.zip" -f $bridgeVersion)
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($artifactRoot, $zipPath,
        [IO.Compression.CompressionLevel]::Optimal, $false)
    & $verifier -ArtifactPath $zipPath -ExpectedBridgeVersion $bridgeVersion -ExpectedProtocol 10
    if (-not $?) { throw 'Final zip verification failed.' }

    $size = (Get-Item -LiteralPath $zipPath).Length
    Write-Output ("package=PASS artifact={0} bytes={1}" -f $zipPath, $size)
    Write-Output ("supportedAdapterCount={0}" -f $supported.Count)
    foreach ($record in $supported | Sort-Object adapterId) {
        Write-Output ("supportedAdapter={0} generation={1} dll={2} requiredPackages={3}" -f $record.adapterId,
            $record.generation, $record.assemblyFile, (@($record.requiredPackageIds) -join ','))
    }
    Write-Output ("excludedAdapterGenerationCount={0}" -f $excluded.Count)
    foreach ($record in $excluded | Sort-Object Adapter, Generation) {
        Write-Output ("excludedAdapter={0} generation={1} reason={2}" -f $record.Adapter,
            $record.Generation, $record.Reason)
    }
    if (-not $KeepStaging) { [IO.Directory]::Delete($stagingRoot, $true) }
}
catch {
    if (Test-Path -LiteralPath $stagingRoot) { [IO.Directory]::Delete($stagingRoot, $true) }
    throw
}
finally {
    if ($KeepStaging -and (Test-Path -LiteralPath $stagingRoot)) {
        Write-Output ("staging={0}" -f $stagingRoot)
    }
}
