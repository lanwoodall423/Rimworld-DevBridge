param(
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][int]$RimWorldProcessId
)

$modRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source = [IO.Path]::GetFullPath((Join-Path $modRoot "DevTools\StagedCore\$Version\RimWorldDevBridge.dll"))
$target = [IO.Path]::GetFullPath((Join-Path $modRoot "1.6\Assemblies\RimWorldDevBridge.dll"))
if (-not $source.StartsWith($modRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $target.StartsWith($modRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved path escaped the bridge mod."
}

$process = Get-Process -Id $RimWorldProcessId -ErrorAction SilentlyContinue
if ($process) { $process.WaitForExit() }
Copy-Item -LiteralPath $source -Destination $target -Force
[IO.File]::WriteAllText(
    (Join-Path $modRoot "DevTools\StagedCore\DEPLOYED.txt"),
    "version=$Version`ndeployedUtc=$([DateTime]::UtcNow.ToString('s'))Z")
