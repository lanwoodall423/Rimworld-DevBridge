$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceRoot = Join-Path $root "Source\RimWorldDevBridge"
$runtime = [IO.File]::ReadAllText((Join-Path $sourceRoot "BridgeRuntime.cs"))
$gameComponent = [IO.File]::ReadAllText((Join-Path $sourceRoot "BridgeGameComponent.cs"))
$allSource = [IO.File]::ReadAllText((Join-Path $sourceRoot "BridgeAdapterCatalog.cs")) + $runtime

function Assert-Absent([string]$Text, [string]$Pattern, [string]$Label) {
    if ($Text -match $Pattern) { throw "$Label contains forbidden pattern: $Pattern" }
}

$bootstrapStart = $runtime.IndexOf("internal static void Bootstrap", [StringComparison]::Ordinal)
$bootstrapEnd = $runtime.IndexOf("public static void OnFinalizeInit", $bootstrapStart, [StringComparison]::Ordinal)
if ($bootstrapStart -lt 0 -or $bootstrapEnd -le $bootstrapStart) { throw "Bootstrap method boundaries were not found." }
$bootstrap = $runtime.Substring($bootstrapStart, $bootstrapEnd - $bootstrapStart)

Assert-Absent $bootstrap "BridgeAdapterCatalog|BridgeOrchestration|BridgeFeatureTests" "dormant bootstrap"
Assert-Absent $bootstrap "TcpListener|System\.Threading\.Timer|new Timer" "dormant bootstrap"
Assert-Absent $bootstrap "typeof\(Root\).*Update|nameof\(OnRootUpdate\)" "dormant bootstrap"
Assert-Absent $gameComponent "GameComponentTick|GameComponentUpdate" "game component"
Assert-Absent $allSource "\.GetTypes\(\)|PatchAll\(" "bridge source"
if ([regex]::Matches($allSource, "AppDomain\.CurrentDomain\.GetAssemblies").Count -gt 1) {
    throw "Only one explicit lazy loaded-assembly identity lookup is allowed."
}

if ($runtime -notmatch "EnsureUpdatePatch\(\)") { throw "On-demand update patch is missing." }
if ($runtime -notmatch "StartDormantWatcher\(\)") { throw "Dormant wake watcher is missing." }
if ($allSource -notmatch 'GetFiles\(BridgePaths\.AdapterPath, "\*\.manifest\.json"\)') {
    throw "Manifest-only adapter indexing is missing."
}

Write-Output "sourceInvariants=PASS dormantTickWork:false appDomainTypeScan:false eagerAdapters:false"
