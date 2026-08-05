$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$sourceRoot = Join-Path $root "Source\RimWorldDevBridge"
$runtime = [IO.File]::ReadAllText((Join-Path $sourceRoot "BridgeRuntime.cs"))
$gameComponent = [IO.File]::ReadAllText((Join-Path $sourceRoot "BridgeGameComponent.cs"))
$diagnostics = [IO.File]::ReadAllText((Join-Path $sourceRoot "BridgeDiagnostics.cs"))
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
Assert-Absent $gameComponent "GameComponentTick|GameComponentUpdate" "game component"
Assert-Absent $diagnostics "AllPawnsSpawned\.ToList\(\)|AllThings\.ToList\(\)" "paged query capture"
if ($diagnostics -notmatch "SnapshotProjectionOperation|currentCount") {
    throw "Cooperative paged query projection is missing."
}
Assert-Absent $allSource "\.GetTypes\(\)|PatchAll\(" "bridge source"
if ([regex]::Matches($allSource, "AppDomain\.CurrentDomain\.GetAssemblies").Count -gt 2) {
    throw "Loaded assembly inspection exceeded the capture and execution binding seams."
}

if ($bootstrap -notmatch "EnsureUpdatePatch\(\)") { throw "Main-thread bootstrap update hook is missing." }
if ($runtime -notmatch "StartDormantWatcher\(\)") { throw "Dormant wake watcher is missing." }
if ($runtime -notmatch "WakeSignal\.Signal\(\)") { throw "Wake signal is missing." }
$wakeStart = $runtime.IndexOf("private static void OnWakeFile", [StringComparison]::Ordinal)
$wakeEnd = $runtime.IndexOf("private static void ProcessPendingFileSignals", $wakeStart, [StringComparison]::Ordinal)
if ($wakeStart -lt 0 -or $wakeEnd -le $wakeStart) { throw "Wake callback boundaries were not found." }
$wakeCallback = $runtime.Substring($wakeStart, $wakeEnd - $wakeStart)
Assert-Absent $wakeCallback "StartTransport|StopTransport|Harmony|BridgePaths\." "wake callback"
if ($allSource -notmatch 'DevTools.*BridgeAdapters' -or
    $allSource -notmatch 'SearchOption\.TopDirectoryOnly') {
    throw "Owner-mod nonrecursive adapter indexing is missing."
}
Assert-Absent $allSource 'SearchOption\.AllDirectories' "adapter indexing"

Write-Output "sourceInvariants=PASS dormantTickWork:false bootstrapHook:lightweight appDomainTypeScan:false eagerAdapters:false"
