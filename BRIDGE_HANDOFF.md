# RimWorld Dev Bridge - Chat Handoff

RimWorld has the standalone **RimWorld Dev Bridge** mod at:

`C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge`

## Queue a Feature Test

Whenever a chat adds a testable feature to any mod, queue a compact adapter-command test for
the user's next RimWorld launch:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Queue-RimWorldFeatureTest.ps1" `
  -Mod "Wildlife" -Feature "Feature name" -Test "Expected behavior" `
  -Command "MOD_TEST_COMMAND" -ExpectContains "PASS"
```

Each invocation creates a unique file, so multiple chats can queue concurrently. The user runs
Dev mode action **RimWorld Dev Bridge > Test Features**. It executes all pending tests, displays
a pass/fail window, archives suites only after every test passes, and writes the concise result to:

`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\RimWorld-DevBridge-FeatureTests-Latest.txt`

Use `FEATURE_TESTS` for queue status or `RUN_FEATURE_TESTS` for a headless bridge run. A test
passes when its command exists, completes without exception, contains every `ExpectContains`
value, and contains none of the `RejectContains` values. The queue is dormant until explicitly
inspected or run.

Failed suites remain in `Pending` with attempt time and failure metadata. Read the latest-results
file, correct the feature or its test, and leave the suite queued; the next **Test Features** run
will retry it automatically.

## Launch RimWorld and Test

Use the bounded launcher to build the current core, start RimWorld with `-quicktest` when it is not
already running, verify the bridge PID/version/protocol/schema, and execute an in-game command:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Launch-And-Test-RimWorld.ps1"
```

Run all queued feature suites with a sandbox write lease and map-ready gate:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Launch-And-Test-RimWorld.ps1" `
  -Command RUN_FEATURE_TESTS -StartupTimeoutSeconds 600
```

The launcher stops only a RimWorld process it started unless `-KeepRunning` is supplied. It never
stops an attached process. Bridge wake has a separate 10-second timeout configurable with
`-BridgeWakeTimeoutSeconds`; game/map startup retains the longer startup timeout. Launch/build/
stdout/stderr logs are stored under `.tura\log`, and failures include bounded tails from those logs
and RimWorld's `Player.log`.

Core version 2.0.2 uses a bridge-owned queue drained from RimWorld's main update loop instead of
depending on `SynchronizationContext.Current`, which is null in RimWorld 1.6. Early wake requests
remain pending until the main loop can activate the transport. Attached games retain the short
bridge-wake timeout; a game started by the launcher uses the bounded startup deadline while mods load.
Forced cleanup removes status/wake/legacy files only when their status PID and boot ID match the
launcher-owned process, so an attached or newer RimWorld session is never cleaned as collateral.

It is local-only, requires RimWorld Dev mode, remains dormant until requested, and keeps a requested TCP session warm for three minutes.

## Session Context

Unless the user explicitly says the connected save represents their current live play, treat every bridge session as a test/sandbox save used for diagnostics and automated actions. Map state, colony composition, selections, activity, progression, and behavior are not representative of how the user normally plays.

Bridge data remains suitable for:

- Reproducing bugs and validating fixes.
- Exercising commands and gameplay systems.
- Inspecting definitions, runtime state, compatibility, and performance.
- Generating feature ideas from available mechanics and content.

Do not use the default test-save state as evidence of player preferences, common workflows, balance outcomes, progression pace, or unmet player needs. Ask for explicit live-play context before making those inferences.

## Required Sync

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Send-RimWorldBridge.ps1" SYNC
```

Run this once at the beginning of every chat. Keep the returned `fp` in context. Later, `SYNC <fp>` returns `same` unless the core, adapters, or hot macros changed. Every `SYNC` also returns:

`context=test-save representativePlayerBehavior:false livePlay:only-when-user-directed`

This context line is protocol-level guidance and must be honored even when the fingerprint is unchanged.

Request format is `id|COMMAND|argument`. Responses begin with `id=` and `status=` and are capped.

Multiple chats may use the PowerShell client concurrently. TCP requests have unique IDs and isolated
responses; up to 16 clients can wait while all RimWorld state access is serialized on the main thread.
Do not use the legacy shared input/output text files for concurrent requests.

After restarting for a bridge update, concurrency can be checked compactly with:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Test-BridgeConcurrency.ps1"
```

## Efficient Workflow

1. Run `SYNC`. Restart RimWorld if it reports `RESTART_REQUIRED` or `restart:1`.
2. Run `HELP` only when the fingerprint changed or a particular command is unclear.
3. Run `RELOAD_HOT_ADAPTERS` after building a new adapter generation.
4. Start with `SNAPSHOT`, an adapter summary such as `AQUACULTURE`, or `CODEX` for Wildlife.
5. Use `BATCH "SNAPSHOT;SELECTED;UI_STATE"` to avoid multiple calls.
6. Batch arguments use a colon: `BATCH "AQUACULTURE;AQUA_POND:42754"`.
7. Prefer narrow follow-ups and adapter summaries over reading RimWorld logs.

`BRIDGE_MANIFEST.txt` is the canonical disk version. The client compares it with the running assembly before every request, so stale runtime code is reported before a command is sent.

## Hot Adapter Reloading

The bridge loads versioned adapter DLLs from bytes at:

`C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\HotAdapters`

Loading from bytes means adapter files are not locked and can be replaced while RimWorld is running.

Commands:

- `RELOAD_HOT_ADAPTERS`: load changed DLLs, rescan providers, and make the newest generation override older commands.
- `HOT_ADAPTER_STATUS`: show the adapter directory, retained generations, and load errors.
- `RELOAD_ADAPTERS`: also loads changed hot adapters before rescanning all providers.
- `RELOAD_BRIDGE`: reload adapters and hot XML macros.

Each changed adapter must use a unique assembly identity. Old generations cannot be unloaded from RimWorld's Mono AppDomain and remain in memory until RimWorld restarts. Keep hot adapters:

- Read-only where possible.
- Free of Harmony patches, defs, ticking, long events, and persistent state.
- Limited to diagnostics, validation, test actions, and opening existing UI.

Restart RimWorld after many adapter generations or any adapter load error. Gameplay assemblies, XML defs, Harmony patches, serialized types, and implementation changes still require a restart.

## Aquaculture Adapter

Aquaculture builds its diagnostic adapter separately from gameplay code:

```powershell
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\AquacultureFishing\DevTools\Build-HotBridgeAdapter.ps1"
& "C:\Games\Steam\steamapps\common\RimWorld\Mods\RimWorldDevBridge\DevTools\Send-RimWorldBridge.ps1" RELOAD_HOT_ADAPTERS
```

Aquaculture commands:

- `AQUACULTURE`
- `AQUA_PONDS`
- `AQUA_POND <proxyThingId|x,z>`
- `AQUA_FISH <thingId>`
- `AQUA_SPECIES [filter]`
- `AQUA_SETTINGS`
- `AQUA_ADAPTER_STATUS`
- `AQUA_PERFORMANCE`
- `AQUA_VALIDATE`
- `AQUA_OPEN_PLANNER <proxyThingId|x,z>`

## Hot XML Macros

Hot macros live at the `hotModule=` path in the bridge status file. They can combine existing core or adapter commands without compiling code. Run `RELOAD_BRIDGE` after changing the hot-command XML.

## Adapter Contract

Any mod or standalone hot adapter can expose commands without referencing the bridge assembly:

```csharp
public static string[] BridgeCommandSpecs() =>
    new[] { "MY_STATUS|R|Compact status", "MY_TEST|W|Run a test" };

public static string BridgeAdapterInfo() =>
    "MyMod|1.0.0|Initial compact bridge commands.";

public static List<string> ExecuteBridgeCommand(string command, string argument, Map map)
{
    // Return brief key:value lines.
}
```

`R` means read-only and `W` means state-changing.

## Update Rule

Every core bridge edit must update `BRIDGE_MANIFEST.txt`, add one compact line to `BRIDGE_CHANGELOG.txt`, and rebuild. Every adapter edit must bump `BridgeAdapterInfo()`. Hot adapter binaries and XML macro contents are fingerprinted automatically.

## Deployment Rule

Do not overwrite a loaded gameplay or bridge DLL while RimWorld is running. Build to a temporary output directory, close RimWorld, deploy the main DLL once, then restart. After that, adapter-only changes can use the hot workflow without restarting.
