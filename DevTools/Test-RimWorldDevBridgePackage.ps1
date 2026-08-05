param(
    [Parameter(Mandatory=$true)][string]$ArtifactPath,
    [string]$ExpectedBridgeVersion = '2.1.0',
    [int]$ExpectedProtocol = 10,
    [string]$ExpectedCorePath
)

$ErrorActionPreference = 'Stop'
$artifact = [IO.Path]::GetFullPath($ArtifactPath)
$temporaryRoot = $null
$root = $null
$errors = New-Object 'System.Collections.Generic.List[string]'
$expectedFiles = @(
    'About/About.xml',
    'AGENTS.md',
    'BRIDGE_HANDOFF.md',
    'LoadFolders.xml',
    'BRIDGE_MANIFEST.txt',
    'DevTools/DEVBRIDGE_AGENT.md',
    'DevTools/Send-RimWorldBridge.ps1',
    'DevTools/devbridge.ps1',
    '1.6/Assemblies/RimWorldDevBridge.dll',
    'RestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe'
)

function Add-Error([string]$Message) { [void]$errors.Add($Message) }

function Test-RawEntryName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Contains('\')) { return 'contains an invalid backslash or is empty' }
    if ($Name.StartsWith('/') -or $Name -match '^[A-Za-z]:') { return 'is rooted or drive-qualified' }
    if ($Name.Contains(':')) { return 'contains a drive-qualified path separator' }
    $parts = $Name.Split('/')
    if ($parts.Count -eq 0) { return 'contains no path segments' }
    foreach ($part in $parts) {
        if ([string]::IsNullOrEmpty($part) -or $part -eq '.' -or $part -eq '..') {
            return 'contains an empty, dot, or traversal path segment'
        }
    }
    return $null
}

function Relative-Path([string]$Path) {
    return $Path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
}

function Test-ForbiddenDll([string]$Name) {
    return $Name -match '^(Assembly-CSharp(?:-firstpass)?|Unity.*|0Harmony|Harmony.*|RimWorldDevBridge\.CompatibilityHarness|BridgeFixtureAdapter)\.dll$'
}

function Test-ForbiddenAssembly([string]$Name) {
    return $Name -match '^(Assembly-CSharp(?:-firstpass)?|Unity.*|0Harmony|Harmony.*|mscorlib|System(?:\..*)?|netstandard)$'
}

function Read-KeyValueFile([string]$Path) {
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $separator = $line.IndexOf('=')
        if ($separator -gt 0) { $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
    }
    return $values
}

function Compare-Core([string]$Expected, [string]$Actual) {
    if ([string]::IsNullOrWhiteSpace($Expected)) { return }
    if (-not (Test-Path -LiteralPath $Expected -PathType Leaf)) {
        Add-Error "Expected source core is missing: $Expected"
        return
    }
    $expectedInfo = Get-Item -LiteralPath $Expected
    $actualInfo = Get-Item -LiteralPath $Actual
    if ($expectedInfo.Length -ne $actualInfo.Length) { Add-Error 'Packaged core is not byte-length identical to the source core.' }
    $expectedHash = (Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash
    if ($expectedHash -cne $actualHash) { Add-Error 'Packaged core SHA-256 differs from the source core.' }
}

try {
    $rawEntries = @()
    if ([IO.Directory]::Exists($artifact)) {
        $root = [IO.Path]::GetFullPath($artifact).TrimEnd('\', '/')
    }
    elseif ([IO.File]::Exists($artifact) -and $artifact.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($artifact)
        try { $rawEntries = @($archive.Entries | ForEach-Object { $_.FullName }) }
        finally { $archive.Dispose() }
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($raw in $rawEntries) {
            $reason = Test-RawEntryName $raw
            if ($reason) { Add-Error "Unsafe raw ZIP entry '$raw': $reason"; continue }
            if (-not $seen.Add($raw)) { Add-Error "Duplicate raw ZIP entry: $raw" }
        }
        if ($errors.Count -gt 0) { throw 'Raw ZIP entry validation failed before extraction.' }
        $temporaryRoot = [IO.Path]::Combine([IO.Path]::GetTempPath(),
            'RimWorldDevBridgePackage-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($artifact, $temporaryRoot)
        $root = [IO.Path]::GetFullPath($temporaryRoot).TrimEnd('\', '/')
    }
    else { throw "Artifact must be an existing package directory or .zip file: $artifact" }

    $files = @([IO.Directory]::GetFiles($root, '*', [IO.SearchOption]::AllDirectories) |
        ForEach-Object { Relative-Path $_ })
    $normalized = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $files) {
        if (-not $normalized.Add($relative)) { Add-Error "Duplicate normalized package path: $relative" }
        $reason = Test-RawEntryName $relative
        if ($reason) { Add-Error "Unsafe package path '$relative': $reason" }
        $leaf = [IO.Path]::GetFileName($relative)
        if ($relative -match '(^|/)(bin|obj)(/|$)' -or $leaf.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) -or
            $leaf -match '(?i)(test|fixture|development|harness)') {
            Add-Error "Development/test output is present: $relative"
        }
        if ($leaf.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and (Test-ForbiddenDll $leaf)) {
            Add-Error "Forbidden game/runtime assembly is present: $relative"
        }
    if ($expectedFiles -notcontains $relative) { Add-Error "Unexpected file in release package: $relative" }
    }
    foreach ($required in $expectedFiles) {
        if ($files -notcontains $required) { Add-Error "Required file is missing: $required" }
    }
    if ($files.Count -ne $expectedFiles.Count) { Add-Error "Expected exactly $($expectedFiles.Count) package files, found $($files.Count)." }

    $loadFoldersPath = Join-Path $root 'LoadFolders.xml'
    if (Test-Path -LiteralPath $loadFoldersPath) {
        try {
            $loadFolders = [xml][IO.File]::ReadAllText($loadFoldersPath)
            $folder = $loadFolders.SelectSingleNode('/loadFolders/v1.6/li')
            if ($null -eq $folder -or $folder.InnerText -ne '1.6') { Add-Error 'LoadFolders.xml does not preserve the v1.6 -> 1.6 mapping.' }
        }
        catch { Add-Error ('LoadFolders.xml is invalid: ' + $_.Exception.Message) }
    }
    $aboutPath = Join-Path $root 'About/About.xml'
    if (Test-Path -LiteralPath $aboutPath) {
        try {
            $about = [xml][IO.File]::ReadAllText($aboutPath)
            $packageId = $about.SelectSingleNode('/ModMetaData/packageId')
            $harmony = @($about.SelectNodes('/ModMetaData/modDependencies/li/packageId') |
                Where-Object { $_.InnerText -ieq 'brrainz.harmony' })
            if ($null -eq $packageId -or $packageId.InnerText -ne 'Lan.RimWorldDevBridge') { Add-Error 'About.xml has an unexpected package ID.' }
            if ($harmony.Count -eq 0) { Add-Error 'About.xml does not preserve the Harmony dependency.' }
        }
        catch { Add-Error ('About.xml is invalid: ' + $_.Exception.Message) }
    }
    $bridgeManifestPath = Join-Path $root 'BRIDGE_MANIFEST.txt'
    if (Test-Path -LiteralPath $bridgeManifestPath) {
        $values = Read-KeyValueFile $bridgeManifestPath
        if ($values['bridge'] -ne $ExpectedBridgeVersion) { Add-Error "BRIDGE_MANIFEST bridge version is not $ExpectedBridgeVersion." }
        if ([int]$values['protocol'] -ne $ExpectedProtocol) { Add-Error "BRIDGE_MANIFEST protocol is not $ExpectedProtocol." }
        foreach ($declaredKey in @('handoff', 'client', 'compatibilityWrapper', 'agentGuide')) {
            $declared = [string]$values[$declaredKey]
            if ([string]::IsNullOrWhiteSpace($declared)) {
                Add-Error "BRIDGE_MANIFEST is missing declared file $declaredKey."
            }
            elseif (-not ($expectedFiles -contains $declared)) {
                Add-Error "BRIDGE_MANIFEST declares an unexpected file $declared."
            }
            elseif (-not (Test-Path -LiteralPath (Join-Path $root ($declared -replace '/', '\')) -PathType Leaf)) {
                Add-Error "BRIDGE_MANIFEST declared file is absent $declared."
            }
        }
    }

    $corePath = Join-Path $root '1.6/Assemblies/RimWorldDevBridge.dll'
    if (Test-Path -LiteralPath $corePath) {
        try {
            $identity = [Reflection.AssemblyName]::GetAssemblyName($corePath)
            if ($identity.Name -ne 'RimWorldDevBridge') { Add-Error 'Core assembly identity is unexpected.' }
            if (Test-ForbiddenAssembly $identity.Name) { Add-Error 'Core assembly is classified as forbidden.' }
            Compare-Core $ExpectedCorePath $corePath
        }
        catch { Add-Error ('Core assembly metadata is invalid: ' + $_.Exception.Message) }
    }
    $coordinatorPath = Join-Path $root 'RestartCoordinator/RimWorldDevBridge.RestartCoordinator.exe'
    if (Test-Path -LiteralPath $coordinatorPath) {
        try {
            $coordinatorIdentity = [Reflection.AssemblyName]::GetAssemblyName($coordinatorPath)
            if ($coordinatorIdentity.Name -ne 'RimWorldDevBridge.RestartCoordinator') {
                Add-Error 'Restart coordinator assembly identity is unexpected.'
            }
        }
        catch { Add-Error ('Restart coordinator metadata is invalid: ' + $_.Exception.Message) }
    }

    $clientPath = Join-Path $root 'DevTools/devbridge.ps1'
    if (Test-Path -LiteralPath $clientPath -PathType Leaf) {
        try {
            $tokens = [System.Management.Automation.Language.Parser]::ParseFile($clientPath,
                [ref]$null, [ref]$null)
            if ($null -eq $tokens) { Add-Error 'Canonical client could not be parsed.' }
        }
        catch { Add-Error ('Canonical client is invalid: ' + $_.Exception.Message) }
    }

    if ($errors.Count -gt 0) {
        Write-Output ("packageVerification=FAIL errors={0}" -f $errors.Count)
        foreach ($error in $errors) { Write-Output ('error=' + $error) }
        exit 1
    }
    Write-Output ("packageVerification=PASS artifact={0} files={1} adapters=0" -f $artifact, $files.Count)
    foreach ($entry in $expectedFiles) { Write-Output ('entry=' + $entry) }
}
finally {
    if ($temporaryRoot -and [IO.Directory]::Exists($temporaryRoot)) { [IO.Directory]::Delete($temporaryRoot, $true) }
}
