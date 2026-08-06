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
$gameArguments = "/c ping -n 3 127.0.0.1 >nul & exit 7"
$testUserRoot = Join-Path ([IO.Path]::GetTempPath()) ("RimWorldDevBridgeLauncherTest-" + [Guid]::NewGuid().ToString("N"))
[IO.Directory]::CreateDirectory($testUserRoot) | Out-Null
$output = $null
try {
    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $launcher -GamePath $env:ComSpec `
        -UserRoot $testUserRoot -ModConfiguration managed-test -GameProcessName RimWorldLauncherSynthetic `
        -NoQuickTest -SkipBuild -GameArguments $gameArguments -StartupTimeoutSeconds $TimeoutSeconds 2>&1
}
finally {
    try {
        Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'RimWorldDevBridge.RestartCoordinator.exe' -and $_.CommandLine -like "*$testUserRoot*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch { }
    if (Test-Path -LiteralPath $testUserRoot) { Remove-Item -LiteralPath $testUserRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
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
