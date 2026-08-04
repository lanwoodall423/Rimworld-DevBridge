# RimWorld Dev Bridge - Codex Handoff

The bridge is installed at:

`C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge`

It is a loopback-only, on-demand development bridge. Treat the connected game as a test/sandbox
unless the user explicitly identifies it as live play. Do not infer player preferences or normal
play patterns from bridge state.

## Start Every Session

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Send-RimWorldBridge.ps1" SYNC
```

Keep the returned `fingerprint`. `SYNC <fingerprint>` reports `same` until the core or active
adapter manifests change. The client compares the loaded bridge version, protocol, and schema with
`BRIDGE_MANIFEST.txt` before sending a request and reports `RESTART_REQUIRED` for stale game code.

Use `CAPABILITIES`, `HELP read`, `HELP write`, `HELP available`, `HELP <adapter>`, or
`DESCRIBE <command>` only when needed. Responses support compact lines and `format=json`.

## Safety

Sessions start read-only. Every non-read command requires all of:

- The bridge setting `Allow explicitly leased remote mutations` enabled.
- `WRITE_LEASE sandbox` or `WRITE_LEASE live-confirmed`.
- The returned short-lived token in `lease=<token>`.
- A stable `idempotency=<key>` for safe retry.
- `allowExpensive=true` for expensive or simulation commands.

Use `RENEW_WRITE_LEASE` with `lease=<token>` to extend an active lease, or
`REVOKE_WRITE_LEASE` with the same token to remove it immediately. Both operations update the
status file and runtime indicator.

Dev mode alone never authorizes a write. Potentially destructive commands require a sandbox lease.
Modes are derived transitively for batches, macros, and feature tests. Mutations produce a summary
and a bounded audit under RimWorld user data.

## Efficient Workflow

1. Run `SYNC`.
2. Start with `STATUS`, `MAP_SUMMARY`, `CODEX`, `AQUACULTURE`, or another adapter summary.
3. Use paged narrow reads such as `THINGS "filter=...&limit=50"`; follow the returned cursor. `PAWNS`,
   `THINGS`, and `JOBS` cursors are versioned immutable snapshots bound to the session, map, filter,
   fields, and stable `thingId` ordering. They expire after bounded retention and reject old offset
   cursors with `snapshot_cursor_required`; other paged commands retain the legacy cursor behavior.
4. Use `BATCH "STATUS;SELECTED;UI_STATE"` to reduce round trips.
5. Run `RELOAD_HOT_ADAPTERS` after publishing a new adapter generation.
6. Use `ADAPTER_HEALTH`, `SCHEDULER_METRICS`, `COMMAND_METRICS`, and `PERFORMANCE` for evidence.

Multiple clients have isolated request IDs and responses. RimWorld/Unity access is serialized on
the main thread through a bounded, deadline-aware queue. Expensive commands require an explicit
override. A timed-out or disconnected queued request is cancelled before execution.

The game shows a small runtime-only bridge indicator whenever transport is active or a write lease
exists. Read-only, sandbox, and live-confirmed states are distinct; live-confirmed write access is
forced visible even if the optional idle read-only display is disabled. The indicator includes client
count and lease context/expiry in its details and tooltip. `PAWNS`, `THINGS`, and `JOBS` use
cooperative bounded steps; legacy synchronous adapters remain compatible but are measured as
non-cooperative and can be quarantined after a serious overrun. Adapter authors can opt into the
versioned `cooperative-v1` provider contract without changing existing synchronous adapters.

## Launch And Test

Build, launch or safely attach, verify the loaded manifest, wake the bridge, and run a command:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Launch-And-Test-RimWorld.ps1"
```

Run queued feature tests against a map-ready sandbox:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Launch-And-Test-RimWorld.ps1" `
  -Command RUN_FEATURE_TESTS -StartupTimeoutSeconds 600
```

The launcher never stops an attached process and stops only a process it started unless
`-KeepRunning` is supplied. Logs are stored at:

`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\RimWorldDevBridge\LauncherLogs`

Run offline checks with:

```powershell
dotnet build "Source\RimWorldDevBridge\RimWorldDevBridge.csproj" -c Release
dotnet build "DevTools\CompatibilityHarness\CompatibilityHarness.csproj" -c Release
& "DevTools\CompatibilityHarness\bin\Release\net472\RimWorldDevBridge.CompatibilityHarness.exe"
& "DevTools\Test-BridgeSourceInvariants.ps1"
& "DevTools\Test-RimWorldLauncher.ps1"
```

Create and verify the distributable package with:

```powershell
& "DevTools\Package-RimWorldDevBridge.ps1" -Build -OutputDirectory "Release"
& "DevTools\Test-RimWorldDevBridgePackage.ps1" -ArtifactPath "Release\RimWorldDevBridge-2.1.0.zip"
```

Packaging stages only files RimWorld loads: `About`, `LoadFolders.xml`, `BRIDGE_MANIFEST.txt`,
the 1.6 core assembly, and validated current file adapters. It excludes harness/build output,
game/Unity/Harmony reference assemblies, development scripts, and loaded-only adapter manifests.

## Feature Tests

Queue a compact typed suite for the next game or current session:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Queue-RimWorldFeatureTest.ps1" `
  -Mod "Wildlife" -Feature "Feature name" -Test "Expected behavior" `
  -Command "MOD_TEST_COMMAND" -ExpectStatus OK -ExpectContains "expected evidence"
```

New queues, disabled suites, bounded completed history, retry metadata, and latest evidence live at:

`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\RimWorldDevBridge\FeatureTests`

Existing `DevTools\FeatureTests\Pending` XML migrates lazily. The Dev mode action
`RimWorld Dev Bridge > Test Features` runs locally; Codex can use `RUN_FEATURE_TESTS` with a sandbox
lease. Suites support requirements, setup/action/assertion/cleanup phases, random seeds, tick and
time budgets, retry limits, mutation declarations, status/schema/exact/boolean/numeric/range/count/
membership/no-exception assertions, and `BLOCKED` prerequisites. Failed suites remain pending.

## Adapters

Activation indexes only `*.manifest.json` sidecars off-thread. It never loads a DLL to discover its
metadata and never scans all AppDomain types. The newest compatible generation per stable adapter ID
becomes active; its exact provider type loads only when one of its commands is requested.

Current distributable file adapters are `AquacultureFishing`, `Flockmaster`,
`HorticultureNovelSeeds`, `KnowledgeFramework`, and `lan.deferredreality.framework`. `Wildlife`
remains a loaded-assembly integration and is intentionally not redistributed; its colliding legacy
`PERFORMANCE` command is exposed as `WILDLIFE_PERFORMANCE` when its external package is loaded.

Publish adapters with `DevTools\Publish-RimWorldBridgeAdapter.ps1`. Publication writes a uniquely
named DLL first and atomically publishes its manifest last, so partial generations are ignored.
The Aquaculture, Flockmaster, and Horticulture build helpers already invoke this publisher.

Old Mono assemblies cannot be unloaded. `ADAPTER_HEALTH` reports selected, superseded, retained,
failed, incompatible, ignored, verification, timing, and estimated retained bytes. Restart after the
configured retained-generation threshold. Do not claim an old generation was unloaded.

Use `DevTools\Remove-UnmanifestedBridgeAdapters.ps1` as a dry run, then add `-Apply` only when the
reported DLLs are intentionally obsolete.

## Diagnostics

Core coverage includes environment, maps, saves, research, paged pawns/things/defs/components/jobs/
designations/logs/Harmony patches, stable session-scoped references, safe primitive-field component
inspection, selection/UI state, full and region screenshots, state captures/diffs, event history,
save/load development copies, narrow mesh refresh, scheduler/command/adapter/process metrics, and
bounded repeated-query benchmarks.

Artifacts are restricted to the bridge user-data directory. The bridge does not expose arbitrary
code execution, shell execution, arbitrary property invocation, unrestricted reflection, or
arbitrary filesystem paths.

## Update Rules

Every core edit must update `BRIDGE_MANIFEST.txt`, add a compact `BRIDGE_CHANGELOG.txt` line, rebuild,
and restart RimWorld. Adapter-only changes use a unique generation plus manifest and can be reloaded
without restarting. Gameplay assemblies, defs, Harmony patches, and serialized types still require
a normal restart.
