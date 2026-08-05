# RimWorld Dev Bridge - Codex Handoff

The bridge location is installation-specific. Supply `--bridge-root`, set
`RIMWORLD_DEVBRIDGE_BRIDGE_ROOT`, or use the bounded platform discovery performed by the canonical
client. Do not assume a Steam or RimWorld installation path.

It is a loopback-only, on-demand development bridge. Treat the connected game as a test/sandbox
unless the user explicitly identifies it as live play. Do not infer player preferences or normal
play patterns from bridge state.

## Start Every Session

```powershell
& ".\DevTools\devbridge.ps1" wake --json
& ".\DevTools\devbridge.ps1" read --command SYNC --json
```

`DevTools/devbridge.ps1` is the canonical client. It resolves paths from explicit arguments,
documented environment variables, user configuration, and bounded platform discovery.
`Send-RimWorldBridge.ps1` is a temporary compatibility wrapper and only delegates to the canonical client.

Keep the returned `fingerprint`. `SYNC <fingerprint>` reports `same` until the core or active
adapter manifests change. The client compares the loaded bridge version, protocol, and schema with
`BRIDGE_MANIFEST.txt` before sending a request and reports `RESTART_REQUIRED` for stale game code.

Use `CAPABILITIES`, `HELP read`, `HELP write`, `HELP available`, `HELP <adapter>`, or
`DESCRIBE <command>` only when needed. Responses support compact lines and `format=json`.

## Safety

Remote mutation is disabled by default. Every non-read command requires all of:

- The bridge setting `Allow remote mutation leases (in-game confirmation still required)` enabled.
- An explicit in-game confirmation for the currently loaded game in the visible bridge warning panel.
- `WRITE_LEASE sandbox` or `WRITE_LEASE live-confirmed`; the context is intent, not proof that a save is disposable.
- The returned short-lived token in `lease=<token>`.
- A stable `idempotency=<key>` for safe retry.
- `allowExpensive=true` for expensive or simulation commands.

The in-game confirmation warns that remote tools may modify or destroy game state and has a visible
revoke control. It is bound to the current session and loaded game, resets on save/game transition,
main-menu return, bridge restart, setting disable, and explicit revocation, and clears all leases when
revoked. No bridge client can create or restore this confirmation.

Use `RENEW_WRITE_LEASE` with `lease=<token>` to extend an active lease only while confirmation remains
valid. `REVOKE_WRITE_LEASE` removes an active lease immediately and remains available as a safety
operation. Both operations update the status file and runtime indicator.

Dev mode and a client label such as `WRITE_LEASE sandbox` never authorize a write. Potentially
destructive commands require a sandbox lease. Modes are derived transitively for batches, macros, and
feature tests. Mutations produce a summary and a bounded audit under RimWorld user data. Stable denial
codes are `remote_mutation_disabled`, `no_game_loaded`, `in_game_confirmation_required`,
`write_lease_required`, `write_lease_invalid`, `write_lease_expired`, and
`write_lease_agent_mismatch`.

## Efficient Workflow

1. Run `DevTools/devbridge.ps1 discover --json`, then `wake --json` if transport is dormant.
2. Query `context --package-id Lan.RimWorldDevBridge --json` and retrieve descriptors with
   `describe --package-id Lan.RimWorldDevBridge --json`.
3. Start with `DevTools/devbridge.ps1 read --command STATUS --json`, `MAP_SUMMARY`, or another adapter summary.
4. Use paged narrow reads such as `DevTools/devbridge.ps1 read --command THINGS --argument "filter=...&limit=50" --json`; follow the returned cursor. `PAWNS`,
   `THINGS`, and `JOBS` cursors are versioned immutable snapshots bound to the session, map, filter,
   fields, and stable `thingId` ordering. They expire after bounded retention and reject old offset
   cursors with `snapshot_cursor_required`; other paged commands retain the legacy cursor behavior.
5. Use `DevTools/devbridge.ps1 adapter reload --package-id <id> --json` after publishing a new adapter generation.
6. Use `DevTools/devbridge.ps1 read --command ADAPTER_HEALTH --json`, `SCHEDULER_METRICS`, `COMMAND_METRICS`, and `PERFORMANCE` for evidence.

Multiple clients have isolated request IDs and responses. RimWorld/Unity access is serialized on
the main thread through a bounded, deadline-aware queue. Expensive commands require an explicit
override. A timed-out or disconnected queued request is cancelled before execution.

Threading boundary: `BridgeTransportServer` performs only bounded socket/protocol work on worker
threads. `BridgeTransportState` binds clients and resources to one transport generation, and stale
workers cannot publish a newer generation. Request preparation, command execution, status/UI work,
and every Verse/Unity read run through the owner game thread. Adapter filesystem indexing uses
immutable main-thread-captured source records and posts status publication back to that thread.

The canonical client supports `discover`, `wake`, `read`, `context`, `describe`, `call`, `mutate`,
`cancel`, `lease acquire|inspect|renew|release`, `adapter publish|reload`, and `restart request|status|wait`.
It emits JSON on stdout and diagnostics on stderr. Idempotency keys are generated for mutations when
omitted and are returned in the JSON response; callers should supply the same key for an intentional retry.
Transport and lease secrets are redacted unless `--unsafe-debug` is explicitly supplied.

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
& ".\DevTools\Launch-And-Test-RimWorld.ps1"
```

Run queued feature tests against a map-ready sandbox:

```powershell
& ".\DevTools\Launch-And-Test-RimWorld.ps1" `
  -Command RUN_FEATURE_TESTS -StartupTimeoutSeconds 600
```

The launcher never stops an attached process and stops only a process it started unless
`-KeepRunning` is supplied. Logs are stored at:

`%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\RimWorldDevBridge\LauncherLogs`

Run offline checks with:

```powershell
& ".\DevTools\Build-RimWorldDevBridge.ps1"
```

The full build requires private RimWorld 1.6 Managed assemblies and Harmony. Set
`RIMWORLD_MANAGED_DIR` and `RIMWORLD_HARMONY_PATH`, or pass the corresponding parameters. The
checked-in `Build/devbridge.build.json.example` contains no machine-specific paths. Portable CI
does not download or publish proprietary game dependencies.

Create and verify the distributable package with:

```powershell
& "DevTools\Package-RimWorldDevBridge.ps1" -Build -OutputDirectory "Release"
& "DevTools\Test-RimWorldDevBridgePackage.ps1" -ArtifactPath "Release\RimWorldDevBridge-2.1.0.zip"
```

Packaging stages ten Dev Bridge-owned files: `About/About.xml`, `AGENTS.md`, `BRIDGE_HANDOFF.md`,
`LoadFolders.xml`, `BRIDGE_MANIFEST.txt`, the 1.6 core assembly, the external `RestartCoordinator/`
executable, `DevTools/devbridge.ps1`, its temporary `Send-RimWorldBridge.ps1` compatibility wrapper,
and `DevTools/DEVBRIDGE_AGENT.md`. It excludes all adapters, external integration files,
harness/build output, game/Unity/Harmony reference assemblies, and test scripts. The package verifier
validates raw ZIP names before extraction, parses the canonical client, and compares the packaged core
byte-for-byte and by SHA-256 with the built source DLL.

## Feature Tests

Queue a compact typed suite for the next game or current session:

```powershell
& ".\DevTools\Queue-RimWorldFeatureTest.ps1" `
  -Mod "owner.package.id" -Feature "Feature name" -Test "Expected behavior" `
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

Activation captures loaded owner mods on the RimWorld main thread, then indexes only each owner
mod's nonrecursive `DevTools/BridgeAdapters` directory off-thread. It never loads a DLL to discover
metadata. The newest compatible generation per stable adapter ID becomes active; its exact provider
type loads only when one of its commands is requested. Owner mods publish and distribute their own
adapter DLLs and manifests under this convention. Dev Bridge owns only the contract, discovery,
validation, execution, safety, and diagnostics infrastructure; participating mods remain usable
without Dev Bridge, and Dev Bridge has no gameplay-mod dependency.

`DevTools/HotAdapters` remains a legacy/development override for one compatibility period and is
never included in a public Dev Bridge package. Owner copies win over identical legacy bindings;
conflicting immutable bindings are quarantined and reported. Source package/kind are included in
health output without exposing absolute paths.

Publish adapters with the integrating mod's own publisher. The existing
`DevTools\Publish-RimWorldBridgeAdapter.ps1` remains useful for development; publication writes a
uniquely named DLL first and atomically publishes its manifest last, so partial generations are
ignored.

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
