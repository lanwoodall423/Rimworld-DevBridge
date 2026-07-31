param([int]$TimeoutSeconds = 10)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$launcher = Join-Path $root "DevTools\Launch-And-Test-RimWorld.ps1"
$tokens = $null
$errors = $null
[Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -ne 0) {
    $errors | ForEach-Object { Write-Output $_.Message }
    exit 1
}

$shell = (Get-Process -Id $PID).Path
$gameArguments = "/c exit 7"
$output = & $shell -NoProfile -ExecutionPolicy Bypass -File $launcher -GamePath $env:ComSpec `
    -NoQuickTest -SkipBuild -GameArguments $gameArguments -StartupTimeoutSeconds $TimeoutSeconds 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object { Write-Output $_ }
if ($exitCode -ne 2 -or -not ($output -match "summary=FAIL reason=rimworld_exited_(7|unknown)")) {
    Write-Output ("launcherRegression=FAIL exit={0}" -f $exitCode)
    exit 1
}
Write-Output "launcherRegression=PASS"

. (Join-Path $root "DevTools\RimWorldLauncherSupport.ps1")
$cleanupRoot = Join-Path ([IO.Path]::GetTempPath()) ("RimWorldDevBridgeLauncher-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($cleanupRoot) | Out-Null
try {
    $statusPath = Join-Path $cleanupRoot "Status.txt"
    $wakePath = Join-Path $cleanupRoot "Wake.request"
    $inputPath = Join-Path $cleanupRoot "In.txt"
    $outputPath = Join-Path $cleanupRoot "Out.txt"
    [IO.File]::WriteAllText($statusPath, "processId=123`nbootId=owned")
    [IO.File]::WriteAllText($wakePath, "")
    [IO.File]::WriteAllText($inputPath, "")
    [IO.File]::WriteAllText($outputPath, "")
    $paths = @($statusPath, $wakePath, $inputPath, $outputPath)
    if (Remove-OwnedBridgeArtifacts -ProcessId 456 -BootId "owned" -StatusPath $statusPath -ArtifactPaths $paths) {
        throw "Foreign process artifacts were removed."
    }
    if (Remove-OwnedBridgeArtifacts -ProcessId 123 -BootId "foreign" -StatusPath $statusPath -ArtifactPaths $paths) {
        throw "Foreign boot artifacts were removed."
    }
    if (-not (Remove-OwnedBridgeArtifacts -ProcessId 123 -BootId "owned" -StatusPath $statusPath -ArtifactPaths $paths)) {
        throw "Owned artifacts were not removed."
    }
    if ($paths | Where-Object { Test-Path -LiteralPath $_ }) { throw "Owned artifacts remain." }
}
finally {
    if (Test-Path -LiteralPath $cleanupRoot) { [IO.Directory]::Delete($cleanupRoot, $true) }
}
Write-Output "launcherCleanupRegression=PASS"
