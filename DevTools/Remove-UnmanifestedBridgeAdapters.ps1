param([switch]$Apply)

$ErrorActionPreference = "Stop"
$adapterRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "HotAdapters"))
if (-not [IO.Directory]::Exists($adapterRoot)) { throw "Adapter directory not found: $adapterRoot" }

$published = @{}
foreach ($manifestPath in [IO.Directory]::GetFiles($adapterRoot, "*.manifest.json")) {
    try {
        $manifest = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($manifestPath)) | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($manifest.assemblyFile)) {
            $published["$($manifest.assemblyFile)"] = $true
        }
    }
    catch {
        throw "Cannot prune while manifest is invalid: $manifestPath ($($_.Exception.Message))"
    }
}

$candidates = @([IO.Directory]::GetFiles($adapterRoot, "*.dll") | Where-Object {
    -not $published.ContainsKey([IO.Path]::GetFileName($_))
})
foreach ($path in $candidates) {
    Write-Output ("unmanifested={0}" -f [IO.Path]::GetFileName($path))
    if ($Apply) { [IO.File]::Delete($path) }
}
Write-Output ("prune={0} count={1} published={2}" -f $(if ($Apply) { "APPLIED" } else { "DRY_RUN" }),
    $candidates.Count, $published.Count)
