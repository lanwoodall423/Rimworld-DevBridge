param([string]$ModsRoot = (Join-Path $PSScriptRoot "..\.."))

$ErrorActionPreference = "Stop"
$validator = Join-Path $PSScriptRoot "Test-DevBridgeAgentDescriptor.ps1"
$root = (Resolve-Path -LiteralPath $ModsRoot -ErrorAction Stop).Path
$repositories = @()
foreach ($directory in @(Get-ChildItem -LiteralPath $root -Directory)) {
    $descriptor = Join-Path $directory.FullName "DevTools\DevBridge\agent.json"
    if (Test-Path -LiteralPath $descriptor -PathType Leaf) { $repositories += $directory.FullName }
}
if ($repositories.Count -eq 0) { throw "descriptorValidation=FAIL no repository descriptors found" }
foreach ($repository in $repositories) { & $validator -RepositoryRoot $repository | Write-Output }
Write-Output ("descriptorValidation=PASS repositories={0} guidance=AGENTS.md+runtimeGuide" -f $repositories.Count)
