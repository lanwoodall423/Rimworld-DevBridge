# RimWorld Dev Bridge - Codex Handoff

The bridge location is installation-specific. Supply `--bridge-root`, set
`RIMWORLD_DEVBRIDGE_BRIDGE_ROOT`, or use the bounded platform discovery performed by the canonical
client. Do not assume a Steam or RimWorld installation path.

It is a loopback-only, on-demand development bridge. Every connected game is live and non-disposable by default. Only the human operator may explicitly identify the currently loaded
game as a disposable sandbox. A client label, command name, save name, dev mode, or inference from
bridge state never proves that a game is disposable.

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
- `WRITE_LEASE sandbox` or `WRITE_LEASE live-confirmed`; the context is client intent only, never proof
  that the current game is disposable. Only the human operator can make that identification.
- The returned short-lived token in `lease=<token>`.
- A stable `idempotency=<key>` for safe retry.
- `allowExpensive=true` for expensive or simulation commands.

The in-game confirmation warns that remote tools may modify or destroy game state and has a visible
revoke control. It is bound to the current session and loaded game, resets on save/game transition,
main-menu return, bridge restart, setting disable, and explicit revocation, and clears all leases when
revoked. No bridge client can create or restore this confirmation.

Mutation audits and status use two separate server-observed identities. `gameIdentity` is a
process-local identity for the in-memory `Verse.Game`; `saveIdentity` is a versioned SHA-256 digest
of the loaded-save value observed from `GameInitData.gameToLoad`, or `none` for a new/unsaved game.
The raw save value, absolute paths, usernames, and credentials are never retained or published.
The digest is scoped to this process/session binding and is not a stable identity across processes.

Use `RENEW_WRITE_LEASE` with `lease=<token>` to extend an active lease only while confirmation remains
valid. `REVOKE_WRITE_LEASE` removes an active lease immediately and remains available as a safety
operation. Both operations update the status file and runtime indicator.

Dev mode and a client label such as `WRITE_LEASE sandbox` never authorize a write. Every connected
game remains live/non-disposable unless the human operator explicitly identifies it as a disposable
sandbox; potentially destructive commands still require a sandbox lease. Modes are derived
transitively for batches, macros, and feature tests. Mutations produce a summary and a bounded audit
under RimWorld user data. Stable denial codes are `remote_mutation_disabled`, `no_game_loaded`, `in_game_confirmation_required`,
`write_lease_required`, `write_lease_invalid`, `write_lease_expired`, and
`write_lease_agent_mismatch`.

## Efficient Workflow

1. Run `DevTools/devbridge.ps1 discover --json`. `bridge_not_active` is recoverable. Activate the authorized managed-test instance and wait for bridge readiness before abandoning runtime verification.
2. Query `context --package-id Lan.RimWorldDevBridge --json` and retrieve descriptors with
   `describe --package-id Lan.RimWorldDevBridge --json`.
3. Start with `DevTools/devbridge.ps1 read --command STATUS --json`, `MAP_SUMMARY`, or another adapter summary.
4. Use paged narrow reads such as `DevTools/devbridge.ps1 read --command THINGS --argument "filter=...&limit=50" --json`; follow the returned cursor. `PAWNS`,
   `THINGS`, and `JOBS` cursors are versioned immutable snapshots bound to the session, map, filter,
   fields, and stable `thingId` ordering. They expire after bounded retention and reject old offset
   cursors with `snapshot_cursor_required`; other paged commands retain the legacy cursor behavior.
5. Use `DevTools/devbridge.ps1 adapter reload --package-id <id> --json` after publishing a new adapter generation.
6. Use `DevTools/devbridge.ps1 read --command ADAPTER_HEALTH --json`, `SCHEDULER_METRICS`, `COMMAND_METRICS`, and `PERFORMANCE` for evidence.

When a read-only runtime operation reports `bridge_not_active`, the canonical client first
signals wake, then uses the authorized managed-test profile through `restart ensure` when the
process is absent, stale, or has mismatched assemblies. Activation uses readiness `bridge`, save
policy `none`, and `--keep-running`; concurrent clients coalesce on one persisted activation
cycle. Progress is emitted as structured JSON diagnostics on stderr. After readiness the client
discards stale context, refreshes discover, context, and `STATUS`, and retries the original
read-only operation once. Startup is bounded by `--startup-timeout-ms`.

Structured activation failures and managed-launch states include `activation_in_progress`,
`activation_timeout`, `managed_profile_missing`, `sandbox_authorization_missing`,
`runtime_build_required`, `deployment_mismatch`, `managed_process_exited_before_ready`,
`stale_managed_ownership_recovered`, `managed_launch_retrying`, `managed_launch_failed`,
`bridge_handshake_timeout`, `bridge_load_failed`, `launch_profile_invalid`, and
`attached_live_process_requires_operator`. A dead coordinator-owned PID is automatically
recoverable after identity validation and bounded replacement attempts; it never produces
`USER_RESTART_REQUIRED`. That result is reserved for a live externally owned process.
Activation never claims or terminates an unrelated manually launched RimWorld process and
never grants mutation authorization or a write lease.

Recovery responses always expose `activationState=inactive|activation_in_progress|ready|failed`,
`waitFor=none|bridge|game|map`, `recoverable`, `requiredAction`, `keepRunning`, `retrySafe`,
`operatorActionRequired`, and `nextAction`. The canonical attached-process reason is
`attached_live_process_requires_operator`; the canonical ready reason is `bridge_ready` with
phase `READY`. `attached_process_user_restart_required`, `attached_process_requires_operator`,
`activation_ready`, and `BRIDGE_READY` are compatibility aliases only. Automatic activation is
limited to `discover`, `context`, `describe`, `read`, `repo context`, and `lease inspect`.
Generic `call`, `mutate`, `cancel`, lease acquire/renew/release, and adapter reload do not
automatically wake, activate, retry, or reuse stale leases.

## Shared Runtime And Human Work

Lifecycle callbacks are coalesced to the newest lifecycle sequence. Scheduler metrics expose
`lifecyclePending`, `lifecycleCoalesced`, and `lifecycleDroppedStale`; owner adoption and lifecycle
generation checks remain authoritative. Compatible pure reads may run concurrently, while restart,
save/load, adapter reload, stateful setup, mutation, and lease writes use the fair serialized runtime
lane. Restart requests coalesce into one cycle and affected clients must reacquire fresh context.

The canonical client provides a durable, redacted human-work queue:
`review request|list|get|resolve|cancel|wait|checkpoint|resume`. Requests contain a stable task ID,
category, exact question, options/recommendation, evidence, dependent and independent work,
branch/commit state, expiration/deduplication keys, and an exact resume operation. A response window
defaults to 60 seconds; unresolved work is checkpointed as `READY_AWAITING_HUMAN`, releases all
resources, and remains resumable. Review or approval never authorizes mutation, attached-process
control, or a write lease.

Multiple clients have isolated request IDs and responses. RimWorld/Unity access is serialized on
the main thread through a bounded, deadline-aware queue. Expensive commands require an explicit
override. A timed-out or disconnected queued request is cancelled before execution.

Threading boundary: `BridgeTransportServer` performs only bounded socket/protocol work on worker
threads. `BridgeTransportState` binds clients and resources to one transport generation, and stale
workers cannot publish a newer generation. Request preparation, command execution, status/UI work,
and every Verse/Unity read run through the owner game thread. Adapter filesystem indexing uses
immutable main-thread-captured source records and posts status publication back to that thread.
RimWorld can call `GameComponent.FinalizeInit` from a loading/long-event worker before owner
adoption. That callback queues inert, sequence-bound lifecycle data and returns without touching
the game, save, UI, settings, or filesystem. The first authoritative `Root.Update` adopts the
owner, drains lifecycle work before dormant return, and executes finalization once; stale or
duplicate notifications are discarded.

The canonical client supports `discover`, `wake`, `read`, `context`, `describe`, `call`, `mutate`,
`cancel`, `lease acquire|inspect|renew|release`, `adapter publish|reload`, and
`restart authorize-sandbox|revoke-sandbox|request|status|wait|ensure`, plus
`review request|list|get|resolve|cancel|wait|checkpoint|resume`.
It emits JSON on stdout and diagnostics on stderr. Idempotency keys are generated for mutations when
omitted and are returned in the JSON response; callers should supply the same key for an intentional retry.
Transport and lease secrets are redacted unless `--unsafe-debug` is explicitly supplied.

Use `validate --layout auto|source|package` to distinguish a source checkout from the strict eleven-file
release package. Source validation reports coordinator `available`, `buildable`, `missing`, `invalid`, or
`missing_build_tooling` states; `--ensure-runtime-tools` may build the coordinator into the documented
source output path. It never treats a missing source-build output as a valid package.

## Optional Codex MCP Adapter

`DevTools/McpServer` is an optional external .NET 8 STDIO adapter for local Codex. It
invokes `DevTools/devbridge.ps1` and never connects to RimWorld internals or embeds MCP
in the game. The reproducible Windows publish and current Codex configuration examples
are in `DevTools/MCP_SERVER.md`. MCP stdout is protocol-only; sanitized diagnostics use
stderr. The adapter exposes goal-level status, context, pure-read, authorized managed
restart, owner validation, and durable review tools. It exposes no gameplay mutation.
Activation and restart annotations remain truthful and approval is not disabled for
external lifecycle control. Review resolution never grants mutation, confirmation,
attached-process control, or a write lease.

For unattended managed verification, persist and validate a `managed-test` launch profile, then use:

```powershell
& ".\DevTools\devbridge.ps1" restart ensure `
  --game-path <validated-executable> --user-data-root <existing-user-root> `
  --mod-configuration managed-test --readiness bridge --save-policy none --keep-running --json
```

An operator can authorize that validated profile once for unattended sandbox control:

```powershell
& ".\DevTools\devbridge.ps1" restart authorize-sandbox `
  --game-path <validated-executable> --user-data-root <existing-user-root> `
  --mod-configuration managed-test --confirm-disposable-sandbox --json
```

The authorization is local to the user root, binds the executable hash and complete launch profile, and
allows later agents to launch or restart only that coordinator-owned managed-test process. It can be
removed with `restart revoke-sandbox`; it never authorizes an attached process, mutation, or write lease.

The profile records the executable, working directory, arguments, user-data root, mod configuration, and
validation time. Ensure coalesces compatible requests by restart cycle, rotates stale coordinator builds,
and returns ownership, ticket, phase, readiness, and next-action fields. Restart coordination does not
grant mutation authority and does not acquire a write lease. A manually attached or live RimWorld process
returns `USER_RESTART_REQUIRED`; no attached process is claimed, stopped, or force-killed.

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

Run queued feature tests only against a map-ready game that the human operator explicitly identified
as a disposable sandbox:

```powershell
& ".\DevTools\Launch-And-Test-RimWorld.ps1" `
  -Command RUN_FEATURE_TESTS -StartupTimeoutSeconds 600
```

The launcher is read-only when attached. It never stops or claims an attached process, rejects stale
attached state with `USER_RESTART_REQUIRED`, and stops only a coordinator-owned process it started unless
`-KeepRunning` is supplied. Supply `-UserRoot` for a pre-existing managed-test user root. Logs are stored at:

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
& "DevTools\Test-RimWorldDevBridgePackage.ps1" -ArtifactPath "Release\RimWorldDevBridge-2.2.0.zip"
```

Packaging stages eleven Dev Bridge-owned files: `About/About.xml`, `AGENTS.md`, `BRIDGE_HANDOFF.md`,
`LoadFolders.xml`, `BRIDGE_MANIFEST.txt`, the 1.6 core assembly, the external `RestartCoordinator/`
executable, `DevTools/devbridge.ps1`, its temporary `Send-RimWorldBridge.ps1` compatibility wrapper,
`DevTools/DEVBRIDGE_AGENT.md`, and the repository `LICENSE`. The MIT notice applies only to
Dev Bridge-owned repository content; it does not grant rights to RimWorld, Unity, Harmony, or
participating mod content. It excludes all adapters, external integration files,
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
`RimWorld Dev Bridge > Test Features` runs locally; Codex can use `RUN_FEATURE_TESTS` only after the
human operator has identified the current game as disposable and confirmed the in-game warning.
Suites support requirements, setup/action/assertion/cleanup phases, random seeds, tick and
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

Restart coalescing is postcondition-aware. Compatible cycles must match readiness, save policy,
requested assembly/build identity, restart-reason category, and requested PID/session/lifecycle
generation while making bounded progress. A new-process or new-assembly request cannot succeed by
retaining the old PID or session. Stale `WAITING_FOR_GAME` cycles are watchdogged and may be
superseded atomically; their waiters follow the replacement cycle and receive fresh context.
