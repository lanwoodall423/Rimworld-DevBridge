param(
    [string]$ModsRoot = (Join-Path $PSScriptRoot '..\..'),
    [string]$RepositoryRoot = "",
    [string]$PackageId = "",
    [switch]$Strict,
    [string]$HarnessPath = (Join-Path $PSScriptRoot 'CompatibilityHarness\bin\Release\net472\RimWorldDevBridge.CompatibilityHarness.exe')
)

$ErrorActionPreference = 'Stop'
$ModsRoot = [IO.Path]::GetFullPath($ModsRoot)
$bridgeRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$descriptorValidator = Join-Path $PSScriptRoot 'Test-DevBridgeAgentDescriptor.ps1'
$targeted = -not [string]::IsNullOrWhiteSpace($RepositoryRoot) -or -not [string]::IsNullOrWhiteSpace($PackageId)
$validated = New-Object System.Collections.Generic.List[string]
$skipped = New-Object System.Collections.Generic.List[string]
$failed = New-Object System.Collections.Generic.List[string]
$targetedIds = New-Object System.Collections.Generic.List[string]
$encoded = New-Object System.Collections.Generic.List[string]

function Add-Skip([string]$id, [string]$reason) {
    $skipped.Add(("{0}:{1}" -f $id, $reason))
}

function Get-CandidateRoots {
    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $resolved = [IO.Path]::GetFullPath($RepositoryRoot)
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "target_repository_missing" }
        return @((Get-Item -LiteralPath $resolved).FullName)
    }
    if (-not [string]::IsNullOrWhiteSpace($PackageId)) {
        $matches = @()
        foreach ($directoryInfo in @(Get-ChildItem -LiteralPath $ModsRoot -Directory)) {
            $descriptorPath = Join-Path $directoryInfo.FullName 'DevTools\DevBridge\agent.json'
            if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { continue }
            try { $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json } catch { continue }
            if ([string]::Equals([string]$descriptor.packageId, $PackageId, [StringComparison]::OrdinalIgnoreCase)) {
                $matches += $directoryInfo.FullName
            }
        }
        if ($matches.Count -eq 0) { throw "target_package_missing" }
        return $matches
    }
    return @(Get-ChildItem -LiteralPath $ModsRoot -Directory | ForEach-Object { $_.FullName })
}

function Validate-Owner([string]$root) {
    if ([string]::Equals([IO.Path]::GetFullPath($root), $bridgeRepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $descriptorPath = Join-Path $root 'DevTools\DevBridge\agent.json'
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) {
        if ($targeted) { throw 'target_descriptor_missing' }
        Add-Skip ([IO.Path]::GetFileName($root)) 'descriptor_missing'
        return $null
    }
    $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
    $id = [string]$descriptor.packageId
    if ($targeted) { $targetedIds.Add($id) }
    & $descriptorValidator -RepositoryRoot $root | Out-Null
    $directory = Join-Path $root ([string]$descriptor.adapterDirectory)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        if ($targeted) { throw "owner_adapter_directory_missing:$id" }
        Add-Skip $id 'adapter_directory_missing'
        return $null
    }
    $files = @(Get-ChildItem -LiteralPath $directory -File)
    $manifests = @($files | Where-Object { $_.Name -like '*.manifest.json' })
    if ($manifests.Count -eq 0) {
        if ($targeted) { throw "owner_manifest_missing:$id" }
        Add-Skip $id 'manifest_missing'
        return $null
    }
    if ($manifests.Count -ne 1) { throw "owner_manifest_count_invalid:$id" }
    $manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw | ConvertFrom-Json
    if ($manifest.adapterId -eq $null -or [string]::IsNullOrWhiteSpace([string]$manifest.adapterId)) { throw "owner_adapter_id_missing:$id" }
    $dlls = @($files | Where-Object { $_.Extension -ieq '.dll' })
    $loaded = [string]$manifest.assemblySource -eq 'loaded'
    if ($loaded) {
        if ($dlls.Count -ne 0) { throw "owner_loaded_dll_present:$id" }
    } elseif ($dlls.Count -ne 1) {
        throw "owner_dll_count_invalid:$id"
    }
    return [pscustomobject]@{ PackageId = $id; Root = $root; AdapterId = [string]$manifest.adapterId }
}

$roots = Get-CandidateRoots
foreach ($root in $roots) {
    try {
        $owner = Validate-Owner $root
        if ($null -eq $owner) { continue }
        $validated.Add($owner.PackageId)
        $encoded.Add($owner.PackageId + '|' + $owner.Root + '|' + $owner.AdapterId)
    }
    catch {
        $id = [IO.Path]::GetFileName($root)
        $failed.Add(("{0}:{1}" -f $id, $_.Exception.Message))
        if ($targeted -or $Strict) { throw }
    }
}

if ($targeted -and $validated.Count -eq 0) { throw 'target_owner_not_validated' }
if ($Strict -and $skipped.Count -gt 0) { throw ('strict_owner_audit_skipped:' + [string]::Join(',', $skipped)) }

if ($validated.Count -gt 0) {
    if (-not (Test-Path -LiteralPath $HarnessPath -PathType Leaf)) { throw "Compatibility harness is missing: $HarnessPath" }
    $previous = $env:RIMWORLD_DEVBRIDGE_OWNER_ADAPTERS
    try {
        $env:RIMWORLD_DEVBRIDGE_OWNER_ADAPTERS = [string]::Join("`n", $encoded)
        & $HarnessPath
        if ($LASTEXITCODE -ne 0) { throw "Compatibility harness failed with exit code $LASTEXITCODE." }
    }
    finally { $env:RIMWORLD_DEVBRIDGE_OWNER_ADAPTERS = $previous }
}

Write-Output ('ownerAdapterIntegration=PASS validated={0} skipped={1} failed={2} targeted={3}' -f
    $validated.Count, $skipped.Count, $failed.Count, $targetedIds.Count)
Write-Output ('validatedIds={0}' -f ([string]::Join(',', $validated)))
Write-Output ('skippedIds={0}' -f ([string]::Join(',', $skipped)))
Write-Output ('failedIds={0}' -f ([string]::Join(',', $failed)))
Write-Output ('targetedIds={0}' -f ([string]::Join(',', $targetedIds)))
