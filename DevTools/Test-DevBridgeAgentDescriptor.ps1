param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [string]$ExpectedPackageId
)

$ErrorActionPreference = "Stop"

function Fail([string]$message) { throw "descriptorValidation=FAIL $message" }

function Resolve-RepositoryPath([string]$root, [string]$relative, [string]$field) {
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
        $relative -match '^[A-Za-z]:') { Fail "$field must be a nonempty repository-relative path" }
    $segments = $relative.Replace('\', '/').Split('/')
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..' -or
            $segment.IndexOf(':') -ge 0) { Fail "$field contains an unsafe path" }
    }
    $rootFull = [IO.Path]::GetFullPath($root)
    $candidate = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $prefix = $rootFull.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "$field escapes the repository root"
    }
    return $candidate
}

$root = (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
$descriptorPath = Join-Path $root "DevTools\DevBridge\agent.json"
if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { Fail "missing descriptor" }
try { $descriptor = Get-Content -LiteralPath $descriptorPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { Fail "invalid JSON: $($_.Exception.Message)" }

if ($descriptor.schemaVersion -ne 1) { Fail "unsupported schemaVersion '$($descriptor.schemaVersion)'" }
$fields = @("packageId", "adapterDirectory", "adapterSource", "buildEntrypoint", "validateEntrypoint", "runtimeGuide")
foreach ($field in $fields) {
    if ([string]::IsNullOrWhiteSpace([string]$descriptor.$field)) { Fail "missing required field $field" }
}
if ($ExpectedPackageId -and $descriptor.packageId -ne $ExpectedPackageId) {
    Fail "package ID mismatch expected=$ExpectedPackageId actual=$($descriptor.packageId)"
}

$resolved = @{}
foreach ($field in @("adapterDirectory", "adapterSource", "buildEntrypoint", "validateEntrypoint", "runtimeGuide")) {
    $resolved[$field] = Resolve-RepositoryPath $root ([string]$descriptor.$field) $field
}
if (-not (Test-Path -LiteralPath $resolved.runtimeGuide -PathType Leaf)) { Fail "runtime guide is missing" }
if (-not (Test-Path -LiteralPath (Join-Path $root "AGENTS.md") -PathType Leaf)) { Fail "AGENTS.md is missing" }

$aboutPath = Join-Path $root "About\About.xml"
if (Test-Path -LiteralPath $aboutPath -PathType Leaf) {
    try { $about = [xml](Get-Content -LiteralPath $aboutPath -Raw) }
    catch { Fail "About.xml is invalid" }
    $aboutId = [string]$about.ModMetaData.packageId
    if ($aboutId -and $aboutId -ne $descriptor.packageId) {
        Fail "descriptor package ID does not match About.xml expected=$aboutId actual=$($descriptor.packageId)"
    }
}

$adapterDirectory = $resolved.adapterDirectory
if (Test-Path -LiteralPath $adapterDirectory -PathType Container) {
    foreach ($manifestPath in @(Get-ChildItem -LiteralPath $adapterDirectory -Filter "*.manifest.json" -File)) {
        try { $manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { Fail "invalid adapter manifest $($manifestPath.Name)" }
        $owned = @($manifest.requiredPackageIds) -contains $descriptor.packageId
        if ($manifest.modulePackageId) { $owned = $owned -or $manifest.modulePackageId -eq $descriptor.packageId }
        if (-not $owned) { Fail "adapter manifest ownership mismatch $($manifestPath.Name)" }
    }
}

Write-Output ("descriptorValidation=PASS packageId={0} repository={1}" -f $descriptor.packageId, $root)
