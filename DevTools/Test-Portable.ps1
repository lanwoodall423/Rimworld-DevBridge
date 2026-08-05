$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
& (Join-Path $PSScriptRoot 'Test-TrackedArtifacts.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Tracked artifact validation failed.' }
$scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)
foreach ($script in $scripts) {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "PowerShell parse failed: $($script.FullName): $($errors -join '; ')" }
}
$manifest = Get-Content -LiteralPath (Join-Path $root 'BRIDGE_MANIFEST.txt')
foreach ($required in @('bridge=', 'protocol=', 'schema=', 'handoff=', 'client=', 'compatibilityWrapper=', 'agentGuide=', 'license=')) {
    if (-not ($manifest -match ('^' + [regex]::Escape($required)))) { throw "BRIDGE_MANIFEST is missing $required" }
}
if (-not ($manifest -match '^license=MIT$') -or -not ($manifest -match '^licenseFile=LICENSE$')) {
    throw 'BRIDGE_MANIFEST license metadata is not MIT/LICENSE.'
}
if ((Get-Content -LiteralPath (Join-Path $root 'LICENSE') -TotalCount 1) -ne 'MIT License') {
    throw 'LICENSE does not contain the MIT License header.'
}
[xml](Get-Content -LiteralPath (Join-Path $root 'LoadFolders.xml')) | Out-Null
Write-Output ('portableChecks=PASS scripts={0}' -f $scripts.Count)
