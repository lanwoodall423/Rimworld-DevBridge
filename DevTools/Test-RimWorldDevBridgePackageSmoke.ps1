param(
    [Parameter(Mandatory = $true)][string]$ArtifactPath
)

$ErrorActionPreference = 'Stop'
$artifact = (Resolve-Path -LiteralPath $ArtifactPath -ErrorAction Stop).Path
if (-not $artifact.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'package smoke test requires a ZIP artifact'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('RimWorldDevBridgeSmoke-' + [Guid]::NewGuid().ToString('N'))
$userRoot = Join-Path $root 'user'
[IO.Directory]::CreateDirectory($root) | Out-Null
[IO.Directory]::CreateDirectory($userRoot) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($artifact, $root)
    $client = Join-Path $root 'DevTools\devbridge.ps1'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $client)

    $help = (& powershell.exe @arguments help --json | Out-String).Trim() | ConvertFrom-Json
    if ($help.client -ne 'devbridge' -or @($help.commands).Count -eq 0) {
        throw 'client help did not expose the canonical command surface'
    }

    $validate = (& powershell.exe @arguments validate --bridge-root $root --json | Out-String).Trim() | ConvertFrom-Json
    if ($validate.valid -ne $true -or $validate.requiredFiles -ne 11) {
        throw 'packaged manifest validation did not pass'
    }

    $discoverOutput = & powershell.exe @arguments discover --bridge-root $root --user-root $userRoot --json 2>$null
    if ($LASTEXITCODE -ne 4) { throw "offline discovery exit code expected=4 actual=$LASTEXITCODE" }
    $discover = ($discoverOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($discover.available -ne $false -or [string]::IsNullOrWhiteSpace($discover.reason)) {
        throw 'offline discovery did not return a structured unavailable result'
    }
    Write-Output 'packageSmoke=PASS help=PASS manifest=PASS offlineDiscovery=PASS'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
