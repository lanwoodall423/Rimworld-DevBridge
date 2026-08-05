param(
    [string]$ModsRoot = (Join-Path $PSScriptRoot '..\..'),
    [string]$HarnessPath = (Join-Path $PSScriptRoot 'CompatibilityHarness\bin\Release\net472\RimWorldDevBridge.CompatibilityHarness.exe')
)

$ErrorActionPreference = 'Stop'
$ModsRoot = [IO.Path]::GetFullPath($ModsRoot)
if (-not (Test-Path -LiteralPath $HarnessPath -PathType Leaf)) { throw "Compatibility harness is missing: $HarnessPath" }
$encoded = New-Object System.Collections.Generic.List[string]
$descriptorValidator = Join-Path $PSScriptRoot 'Test-DevBridgeAgentDescriptor.ps1'
$bridgeRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$owners = @()
foreach ($directoryInfo in @(Get-ChildItem -LiteralPath $ModsRoot -Directory)) {
    $root = $directoryInfo.FullName
    if ([string]::Equals([IO.Path]::GetFullPath($root), $bridgeRepositoryRoot,
        [StringComparison]::OrdinalIgnoreCase)) { continue }
    $descriptorPath = Join-Path $root 'DevTools\DevBridge\agent.json'
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { continue }
    & $descriptorValidator -RepositoryRoot $root | Out-Null
    $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
    $directory = Join-Path $root $descriptor.adapterDirectory
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Owner adapter directory is missing: $directory" }
    $files = @(Get-ChildItem -LiteralPath $directory -File)
    $manifests = @($files | Where-Object { $_.Name -like '*.manifest.json' })
    if ($manifests.Count -eq 0) { throw "$($descriptor.packageId) has no current BridgeAdapters manifest." }
    $dlls = @($files | Where-Object { $_.Extension -ieq '.dll' })
    if ($manifests.Count -ne 1) { throw "$($descriptor.packageId) must contain exactly one manifest." }
    $manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw | ConvertFrom-Json
    if ($manifest.adapterId -eq $null -or [string]::IsNullOrWhiteSpace([string]$manifest.adapterId)) {
        throw "$($descriptor.packageId) manifest has no stable adapter ID."
    }
    $loaded = [string]$manifest.assemblySource -eq 'loaded'
    if ($loaded) {
        if ($dlls.Count -ne 0) { throw "$($descriptor.packageId) loaded integration must not ship an adapter DLL." }
    } elseif ($dlls.Count -ne 1) {
        throw "$($descriptor.packageId) must contain exactly one adapter DLL."
    }
    $owners += $descriptor.packageId
    $encoded.Add($descriptor.packageId + '|' + $root + '|' + $manifest.adapterId)
}

if ($encoded.Count -eq 0) { throw "No owner repositories with current BridgeAdapters manifests were discovered." }

$previous = $env:RIMWORLD_DEVBRIDGE_OWNER_ADAPTERS
try {
    $env:RIMWORLD_DEVBRIDGE_OWNER_ADAPTERS = [string]::Join("`n", $encoded)
    & $HarnessPath
    if ($LASTEXITCODE -ne 0) { throw "Compatibility harness failed with exit code $LASTEXITCODE." }
} finally {
    $env:RIMWORLD_DEVBRIDGE_OWNER_ADAPTERS = $previous
}

Write-Output ('ownerAdapterIntegration=PASS owners={0}' -f $owners.Count)
