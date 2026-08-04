param(
    [Parameter(Mandatory=$true)][string]$ArtifactPath,
    [string]$ExpectedBridgeVersion = "2.1.0",
    [int]$ExpectedProtocol = 10
)

$ErrorActionPreference = "Stop"
$artifact = [IO.Path]::GetFullPath($ArtifactPath)
$temporaryRoot = $null
$root = $null
$errors = New-Object 'System.Collections.Generic.List[string]'
$adapterIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$adapterFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

function Add-Error([string]$Message) {
    [void]$errors.Add($Message)
}

function Relative-Path([string]$Path) {
    return $Path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
}

function Read-KeyValueFile([string]$Path) {
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) { $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
    }
    return $values
}

function Test-ForbiddenDll([string]$Name) {
    return $Name -match '^(Assembly-CSharp(?:-firstpass)?|Unity.*|0Harmony|Harmony.*|RimWorldDevBridge\.CompatibilityHarness|BridgeFixtureAdapter)\.dll$'
}

function Test-ForbiddenAssembly([string]$Name) {
    return $Name -match '^(Assembly-CSharp(?:-firstpass)?|Unity.*|0Harmony|Harmony.*|mscorlib|System(?:\..*)?|netstandard)$'
}

try {
    if ([IO.Directory]::Exists($artifact)) {
        $root = $artifact
    }
    elseif ([IO.File]::Exists($artifact) -and $artifact.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $temporaryRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(),
            "RimWorldDevBridgePackage-" + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($artifact, $temporaryRoot)
        $root = $temporaryRoot
    }
    else {
        throw "Artifact must be an existing package directory or .zip file: $artifact"
    }

    $root = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
    $files = @([IO.Directory]::GetFiles($root, '*', [IO.SearchOption]::AllDirectories) |
        ForEach-Object { Relative-Path $_ })
    if ($files.Count -eq 0) { Add-Error "The package contains no files." }

    foreach ($relative in $files) {
        $leaf = [IO.Path]::GetFileName($relative)
        if ($relative -match '(^|/)(bin|obj)(/|$)' -or $leaf.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase)) {
            Add-Error "Development output is present: $relative"
        }
        if ($leaf.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and (Test-ForbiddenDll $leaf)) {
            Add-Error "Forbidden game/runtime assembly is present: $relative"
        }
        $allowed = $relative -eq 'LoadFolders.xml' -or $relative -eq 'BRIDGE_MANIFEST.txt' -or
            $relative -eq 'About/About.xml' -or $relative -eq '1.6/Assemblies/RimWorldDevBridge.dll' -or
            $relative -match '^DevTools/HotAdapters/[^/]+\.(manifest\.json|dll)$'
        if (-not $allowed) { Add-Error "Unexpected file in release package: $relative" }
    }

    foreach ($required in @('About/About.xml', 'LoadFolders.xml', 'BRIDGE_MANIFEST.txt',
            '1.6/Assemblies/RimWorldDevBridge.dll')) {
        if (-not $files.Contains($required)) { Add-Error "Required file is missing: $required" }
    }

    $loadFoldersPath = Join-Path $root 'LoadFolders.xml'
    if (Test-Path -LiteralPath $loadFoldersPath) {
        try {
            $loadFolders = [xml][IO.File]::ReadAllText($loadFoldersPath)
            $folder = $loadFolders.SelectSingleNode('/loadFolders/v1.6/li')
            if ($null -eq $folder -or $folder.InnerText -ne '1.6') {
                Add-Error 'LoadFolders.xml does not preserve the v1.6 -> 1.6 mapping.'
            }
        }
        catch { Add-Error ("LoadFolders.xml is invalid: " + $_.Exception.Message) }
    }

    $aboutPath = Join-Path $root 'About/About.xml'
    if (Test-Path -LiteralPath $aboutPath) {
        try {
            $about = [xml][IO.File]::ReadAllText($aboutPath)
            $packageId = $about.SelectSingleNode('/ModMetaData/packageId')
            $harmony = $about.SelectNodes('/ModMetaData/modDependencies/li/packageId') |
                Where-Object { $_.InnerText -ieq 'brrainz.harmony' }
            if ($null -eq $packageId -or $packageId.InnerText -ne 'Lan.RimWorldDevBridge') {
                Add-Error 'About.xml has an unexpected package ID.'
            }
            if ($null -eq $harmony) { Add-Error 'About.xml does not preserve the Harmony dependency.' }
        }
        catch { Add-Error ("About.xml is invalid: " + $_.Exception.Message) }
    }

    $bridgeManifestPath = Join-Path $root 'BRIDGE_MANIFEST.txt'
    if (Test-Path -LiteralPath $bridgeManifestPath) {
        $manifestValues = Read-KeyValueFile $bridgeManifestPath
        if ($manifestValues['bridge'] -ne $ExpectedBridgeVersion) {
            Add-Error ("BRIDGE_MANIFEST bridge version is not {0}." -f $ExpectedBridgeVersion)
        }
        if ([int]$manifestValues['protocol'] -ne $ExpectedProtocol) {
            Add-Error ("BRIDGE_MANIFEST protocol is not {0}." -f $ExpectedProtocol)
        }
    }

    $corePath = Join-Path $root '1.6/Assemblies/RimWorldDevBridge.dll'
    if (Test-Path -LiteralPath $corePath) {
        try {
            $coreIdentity = [Reflection.AssemblyName]::GetAssemblyName($corePath)
            if ($coreIdentity.Name -ne 'RimWorldDevBridge') { Add-Error 'Core assembly identity is unexpected.' }
        }
        catch { Add-Error ("Core assembly metadata is invalid: " + $_.Exception.Message) }
    }

    $adapterRoot = Join-Path $root 'DevTools/HotAdapters'
    $adapterManifestPaths = if (Test-Path -LiteralPath $adapterRoot) {
        @([IO.Directory]::GetFiles($adapterRoot, '*.manifest.json'))
    } else { @() }
    $adapterDllPaths = if (Test-Path -LiteralPath $adapterRoot) {
        @([IO.Directory]::GetFiles($adapterRoot, '*.dll'))
    } else { @() }
    $manifestAssemblyFiles = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($manifestPath in $adapterManifestPaths) {
        $relativeManifest = Relative-Path $manifestPath
        try {
            $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
            if ($manifest.manifestVersion -ne 1 -and $manifest.manifestVersion -ne 2) {
                Add-Error "$relativeManifest has an unsupported manifestVersion."
            }
            if ([string]::IsNullOrWhiteSpace($manifest.adapterId)) { Add-Error "$relativeManifest has no adapterId." }
            elseif (-not $adapterIds.Add([string]$manifest.adapterId)) {
                Add-Error "Duplicate packaged adapter identity: $($manifest.adapterId)"
            }
            if ($manifest.protocolMin -gt $ExpectedProtocol -or $manifest.protocolMax -lt $ExpectedProtocol) {
                Add-Error "$relativeManifest does not support protocol $ExpectedProtocol."
            }
            if ([string]::IsNullOrWhiteSpace($manifest.providerType)) { Add-Error "$relativeManifest has no providerType." }
            if (-not [string]::IsNullOrWhiteSpace($manifest.executionContract) -and
                -not [string]::Equals($manifest.executionContract, 'cooperative-v1', [StringComparison]::OrdinalIgnoreCase)) {
                Add-Error "$relativeManifest declares an unsupported executionContract."
            }
            if ([string]::IsNullOrWhiteSpace($manifest.assemblyFile) -or
                [IO.Path]::GetFileName($manifest.assemblyFile) -ne $manifest.assemblyFile -or
                -not $manifest.assemblyFile.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
                Add-Error "$relativeManifest has an unsafe or missing assemblyFile."
                continue
            }
            if (-not [string]::IsNullOrWhiteSpace($manifest.assemblySource) -and
                -not [string]::Equals($manifest.assemblySource, 'file', [StringComparison]::OrdinalIgnoreCase)) {
                Add-Error "$relativeManifest is not a distributable file adapter."
                continue
            }
            if (-not $manifestAssemblyFiles.Add($manifest.assemblyFile)) {
                Add-Error "Duplicate packaged adapter assembly: $($manifest.assemblyFile)"
            }
            $dllPath = Join-Path $adapterRoot $manifest.assemblyFile
            if (-not (Test-Path -LiteralPath $dllPath)) {
                Add-Error "$relativeManifest references missing $($manifest.assemblyFile)."
                continue
            }
            $fileInfo = Get-Item -LiteralPath $dllPath
            if ([int64]$manifest.assemblyBytes -ne [int64]$fileInfo.Length) {
                Add-Error "$relativeManifest assemblyBytes does not match $($manifest.assemblyFile)."
            }
            $actualHash = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash
            if (-not [string]::Equals($actualHash, [string]$manifest.contentHash, [StringComparison]::OrdinalIgnoreCase)) {
                Add-Error "$relativeManifest contentHash does not match the packaged DLL; no hash was rewritten."
            }
            $assemblyIdentity = [Reflection.AssemblyName]::GetAssemblyName($dllPath).FullName
            $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($dllPath).Name
            if (Test-ForbiddenAssembly $assemblyName) {
                Add-Error "$relativeManifest packages a game/runtime assembly: $assemblyName"
            }
            if ($assemblyIdentity -ne [string]$manifest.assemblyIdentity) {
                Add-Error "$relativeManifest assemblyIdentity does not match the packaged DLL."
            }
            if ($null -eq $manifest.commands -or @($manifest.commands).Count -eq 0) {
                Add-Error "$relativeManifest has no command contract."
            }
            else {
                $commands = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                foreach ($command in @($manifest.commands)) {
                    if ([string]::IsNullOrWhiteSpace($command.name) -or -not $commands.Add([string]$command.name)) {
                        Add-Error "$relativeManifest has a missing or duplicate command name."
                    }
                }
            }
        }
        catch { Add-Error ("Cannot validate {0}: {1}" -f $relativeManifest, $_.Exception.Message) }
    }

    foreach ($dllPath in $adapterDllPaths) {
        $name = [IO.Path]::GetFileName($dllPath)
        if (-not $manifestAssemblyFiles.Contains($name)) {
            Add-Error "Unmanifested loadable adapter DLL is present: $(Relative-Path $dllPath)"
        }
    }

    if ($errors.Count -gt 0) {
        Write-Output ("packageVerification=FAIL errors={0}" -f $errors.Count)
        foreach ($error in $errors) { Write-Output ("error=" + $error) }
        exit 1
    }

    Write-Output ("packageVerification=PASS artifact={0} files={1} adapters={2}" -f
        $artifact, $files.Count, $adapterManifestPaths.Count)
    foreach ($manifestPath in ($adapterManifestPaths | Sort-Object)) {
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
        Write-Output ("adapter={0} generation={1} dll={2}" -f $manifest.adapterId,
            $manifest.generation, $manifest.assemblyFile)
    }
}
finally {
    if ($temporaryRoot -and [IO.Directory]::Exists($temporaryRoot)) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}
