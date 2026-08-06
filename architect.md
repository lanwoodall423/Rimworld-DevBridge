# RimWorld Dev Bridge architecture

## Objective

Provide an on-demand, local-only bridge that gives development clients compact,
typed evidence from RimWorld while adding effectively no gameplay cost when it is
installed but unused. RimWorld and Unity state is accessed only on the game main
thread. Filesystem indexing, hashing, protocol validation, and report storage are
performed off-thread or by stateless external tools.

## Baseline evidence (1.4.1 / protocol 9)

- The unchanged project builds with zero warnings and errors.
- `Game.FinalizeInit` calls `BridgeHost.Initialize`, which calls
  `ReloadProviders` before creating the wake watcher.
  - Provider reload scanned types from every loaded AppDomain assembly, initialized
    macros, enumerated and hashed every hot DLL, and loaded each unseen DLL from
    bytes. That historical session retained 80 hot generations across several
    external owners (4,676,096 source bytes total).
- First wake plus `SYNC` measured about 1,277 ms in the live baseline. Subsequent
  local PowerShell calls measured about 18-33 ms before the process exited.
- The active game process had about 11.1 GB working set and 14.5 GB private bytes;
  this whole-process value cannot isolate bridge memory, so no bridge-only memory
  claim is made for the baseline.
- Missing `THING`, `PAWN`, and `INSPECT` targets returned `status=OK` with a
  `not_found` line.
- TCP workers post unbounded callbacks to `SynchronizationContext` and wait 30
  seconds. A timeout or disconnect does not cancel the posted callback, so it may
  execute later. There is no request deadline, session identity, cancellation,
  write lease, or idempotency cache.
- Adapter `R`/`W` metadata is used only by help text. Macro mutation declarations
  are trusted and nested command modes are not derived. Batches have no enforced
  aggregate mode.
- Responses are capped by line count only. Large collections use fixed `Take`
  limits without stable cursors. Generic inspection invokes public property
  getters.
- Feature tests are substring assertions parsed on demand but their queue and
  history live under the mod installation. Lifecycle initialization is attached
  only to `Game.FinalizeInit`; no explicit game-unload/session invalidation path
  exists.

## Options considered

| Design | Dormant potential | Activation | Complexity/reliability | Decision |
| --- | --- | --- | --- | --- |
| Harden current monolith | Low after extensive surgery | Good | Coupled transport, reflection, commands, lifecycle, and serialization remain hard to verify | Rejected |
| Minimal in-game core | Watcher and fixed status only | Best; no process spawn | Clear main-thread and safety boundary; easiest to test | Selected |
| In-game shim plus persistent external host | Similar shim cost | Adds process startup and another IPC hop | More failure modes; cannot move RimWorld access out of process | Rejected as a required service |

Stateless PowerShell tools remain useful for manifest publication, queueing tests,
retry coordination, and large report management. They run only when requested and
cannot bypass the in-game authorization boundary.

`DevTools/Launch-And-Test-RimWorld.ps1` is the bounded end-to-end operator tool. It
uses an explicit existing user root and a validated `managed-test` launch profile,
launches through the external coordinator only when no RimWorld process is active,
binds readiness to the observed process ID and disk manifest, and invokes the normal
authenticated client. Attached processes are read-only and stale attached state returns
`USER_RESTART_REQUIRED`; it only stops a coordinator-owned process it launched.

The canonical client separates source/package validation from runtime readiness. `validate` reports
layout, missing files, coordinator identity, and build-tool availability without requiring a packaged
coordinator in a source checkout. `restart ensure` persists executable, working directory, arguments,
user-data root, mod configuration, and validation time; it starts only a validated coordinator-owned
profile, detects and replaces a stale coordinator serving the same coordinator root, coalesces compatible
restart requests, and preserves `keep-running`. Coordinator ownership is not mutation authority: restart
does not acquire a write lease, and attached/live game processes remain under human or external-orchestrator
control.

## Selected architecture

1. **Bootstrap and lifecycle**
   - The mod constructor records paths, installs one explicit game-change patch and
     one lightweight `Root.Update` signal/drain hook, creates one save-data wake
     watcher, registers process shutdown, and writes a fixed dormant status.
      `GameComponent.FinalizeInit` supplies game readiness. RimWorld may invoke
      this callback from a loading/long-event worker before the authoritative
      Unity thread has been observed; the callback therefore queues inert
      lifecycle data only. `Root.Update` is the sole owner-adoption point and
      drains that lifecycle queue before inactive-mode return.
   - Dormant mode performs no provider scan, adapter load, macro/test parse,
     fingerprint, map scan, timer, or TCP work; the permanent update hook only
     checks coalesced file signals and returns.
   - The `Root.Update` hook is installed during safe bootstrap, activates transport
     and main-thread draining only after a signal, and is removed at shutdown.
   - Every loaded game receives a random session ID. Game replacement/unload
     rotates authorization, cancels queued requests, and invalidates references.

2. **Transport and protocol boundary**
    - Loopback TCP starts only after a wake request and stops after bounded idle.
    - `BridgeTransportServer` owns worker-side accept, authentication framing,
      request preparation handoff, enqueue/wait, and client cleanup. Its
      `BridgeTransportState` is generation-scoped and contains no RimWorld or
      Unity access. `BridgeTransportAuthentication` is a pure constant-time
      token/parser boundary.
    - Requests are byte-bounded and parsed into `BridgeRequest`; responses are
      `BridgeResult` values with status, schema, timing, provider, mode, mutation,
      truncation, warnings, and structured fields.
   - Protocol 9 line requests remain accepted. Protocol 10 adds key/value request
     options and JSON output without changing the legacy PowerShell entry point.

3. **Main-thread scheduler**
    - Background clients enqueue into bounded per-agent FIFO queues. Deterministic
      round-robin selection prevents one agent from monopolizing a drain while
      preserving ordering within an agent. Only one scheduled main-thread drain
      callback exists at a time.
   - Drain work has a per-frame time/operation budget. Expired, cancelled,
     disconnected, stale-session, unauthorized, or invalid requests are rejected
     before command execution.
    - Cost classes, operation/time drain budgets, bounded core scans, and one
      expensive operation per drain limit bridge-owned stalls. Deliberately
      expensive operations require an override. Built-in large scans use
      cooperative resumable steps. Adapters are cooperative only when they
      explicitly opt into the versioned contract; legacy synchronous code is
      measured, reported as non-cooperative, and cannot be safely preempted.

## Runtime boundaries and threading

- `BridgeRuntime` owns lifecycle, session and transport-generation invalidation,
  the main-thread boundary, shared state publication, status files, and the
  coordinator-facing lifecycle hooks. It is deliberately the composition root,
  not a second command or adapter implementation. Its lifecycle transition
  sequence, owner adoption, session rotation, and transport-generation checks
  remain in this root so no extracted service can publish stale game state.
- `BridgeLifecycleDispatch` owns deferred `FinalizeInit` delivery, while
  `BridgeLeaseExpiryScheduler` owns timer-to-owner-thread handoff. Both carry
  only immutable sequence/session/generation data across the worker boundary;
  `BridgeRuntime.CompleteFinalizeInit` and the lease callback retain the
  authoritative rechecks.
- `BridgeFileActivation` owns the dormant watcher and coalesced wake/input
  signals. `BridgeLegacyFileProtocol` owns bounded file request parsing,
  preparation, enqueueing, and atomic response writing. Neither component
  reads Verse state off-thread.
- `BridgeRequestPreparation` owns the worker-to-owner preparation handoff and
  deadline/cancellation checks. `BridgeScheduledRequestExecutor` owns the
  second authorization, execution, stale-transport, cooperative-yield, audit,
  and completion boundary. Authentication, lease invalidation, and session
  rotation remain owned by `BridgeAuthorization` and `BridgeRuntime`.
- `BridgeStatusPublisher` owns status serialization, consistency checks, status
  locking, file metrics, and atomic publication. `BridgeFileOperations` owns
  safe deletion and atomic UTF-8 writes. `BridgeRuntime` supplies snapshots and
  decides when a publication is valid.
- `BridgeTransportServer` owns only sockets and worker-side protocol handling.
  It may parse bounded bytes, authenticate, enqueue, wait, and close clients.
  It must never read `Current`, `Find`, `LoadedModManager`, maps, UI, Unity, or
  any other RimWorld state. It asks `BridgeRuntime` to prepare work on the main
  thread through a callback boundary.
- `BridgeTransportState` owns resources for one transport generation. A stale
  worker may close only its own clients/resources; it cannot publish state for a
  newer generation.
- `BridgeMetrics` owns command timing/overrun/agent-attribution aggregation and
  has no game-thread dependency. `BridgeTransportAuthentication` owns only
  bounded, constant-time token comparison and framing validation.
- `BridgeScheduler`, `BridgeAuthorization`, `BridgeAdapterCatalog`, and
  `BridgeQuerySnapshotStore` remain independently testable owners of scheduling,
  leases, adapter generations, and immutable query snapshots respectively.
- Adapter responsibilities are split without introducing a provider interface:
  `BridgeAdapterSourceDiscovery` captures owner-thread source/module snapshots;
  `BridgeAdapterManifestValidation` validates immutable manifest candidates;
  `BridgeAdapterAssemblyVerification` performs bounded path, origin, identity,
  and hash checks; `BridgeAdapterGenerationStore` resolves duplicates, selects
  and retains generations, rebuilds command ownership, and computes
  fingerprints; `BridgeAdapterLoader` performs exact lazy provider loading;
  `BridgeAdapterExecution` runs pinned providers and owns circuit health.
- Diagnostic responsibilities are split into `BridgeDiagnosticCommands` for
  the stable 28-command registration/dispatch contract,
  `BridgeSnapshotProjection` for bounded cooperative collection,
  `BridgeDiagnosticArtifacts` for capture/diff serialization, and
  `BridgeEventJournal` for bounded concurrent event storage and paging.
- All Verse/Unity reads and all command execution occur on the owner game thread.
  Filesystem hashing/indexing and socket work may run off-thread only when their
  inputs are immutable snapshots and their callbacks return through the main
  thread dispatcher. `FinalizeInit` worker callbacks carry only a transition
  sequence; they never capture or dereference `Current.Game`, settings, save
  metadata, UI, or paths. The owner thread performs finalization once, drops
  obsolete sequence callbacks, and treats repeated notifications as harmless.

### Threading contract

Background-thread-safe entry points accept immutable request/source snapshots:
`BridgeRuntime.OnGameChanging`, `BridgeRequestPreparation.Prepare` when called
by the transport worker, adapter source capture results after
`BridgeAdapterSourceDiscovery.Capture`, manifest parsing and validation,
assembly verification, and status/audit file writes. These paths must not
dereference `Current.Game`, maps, UI, Unity objects, or mutable loaded-package
state.

Main-thread-only methods include `BridgeRuntime.OnRootUpdate`, finalize-init
completion, transport start/stop, lease confirmation/revocation and settings
application, `BridgeLegacyFileProtocol.Process`, the owner callback inside
`BridgeRequestPreparation`, `BridgeScheduledRequestExecutor.Execute`, all core
diagnostic handlers, lazy adapter provider execution, and snapshot projection
steps. `BridgeMainThreadContext.AdoptOwnerThread` is the sole adoption point;
ordinary callbacks cannot establish ownership.

4. **Authorization and idempotency**
    - Every connected game is live and non-disposable by default. Only the human
      operator may explicitly identify the currently loaded game as a disposable
      sandbox; a client label, command, naming convention, dev mode, or inference
      never proves sandbox status. Remote mutation is disabled by default. A server-controlled, runtime-only
      confirmation must be made in-game for the currently loaded Game/save before
      a lease can be issued or honored. A client context label is intent only.
       Confirmation is bound to the session, process-local Game identity, and independent
       server-observed save identity, is
      visibly revocable, and clears leases on revocation, setting disable, game
      transition, main-menu return, and bridge restart.
   - Writes then require an explicit short-lived agent-owned lease bound to the
      session and declared sandbox/live context. Stable denial codes distinguish
      disabled mutation, no game, missing confirmation, and invalid/expired/
      wrong-agent leases.
   - Command, macro, batch, and feature-test modes are derived transitively.
     Unknown commands are forbidden rather than implicitly safe.
    - Completed writes are cached by session plus idempotency key. A retry returns
      the original result. A bounded audit records every accepted mutation with
       server-observed game identity, independent save identity, confirmation state, setting
       state, lease context, and expiry, never transport or lease tokens. Game identity is
       process-local. Save identity is a versioned digest of the loaded-save value or `none`
       for a new/unsaved game; raw save metadata and paths are not retained or published.

5. **Commands and inspection**
    - Core commands use typed handlers and meaningful statuses. Lists use stable
      ordering, requested fields, byte/scan budgets, `limit`, and opaque cursors.
      `PAWNS`, `THINGS`, and `JOBS` establish bounded immutable DTO snapshots on
      page one and use versioned snapshot cursors on later pages; old offset
      cursors are rejected for those commands while other paged commands remain
      wire-compatible.
   - Object references carry session, map, and object IDs. No live object is cached
     across requests or sessions.
   - Generic inspection reads fields and an explicit safe-property allowlist only;
     adapters own complex inspection.

6. **Adapters**
    - On the main thread, the bridge captures exact loaded owner package IDs,
      versions, normalized roots, and loaded module bindings. Off-thread indexing
      then scans only each owner's nonrecursive `DevTools/BridgeAdapters` directory.
      The newest compatible manifest per stable adapter ID is selected without
      loading DLLs.
    - `DevTools/HotAdapters` is a bounded legacy/development source for one
      compatibility period, not a public distribution channel. Owner-mod copies
      take precedence over identical legacy bindings; conflicting immutable
      bindings are quarantined rather than selected.
   - A selected assembly loads only when one of its commands is requested. Exact
     provider types avoid assembly-wide type scans. Malformed, partial,
     incompatible, colliding, and failing generations are isolated and reported.
   - Live replacements switch command ownership atomically. Mono-retained old
     generations are reported honestly and trigger a restart recommendation at a
     configurable threshold.
    - Legacy convention providers require an explicit manifest with an exact
      loaded assembly identity and provider type. They are resolved only on first
      command and never trigger assembly/type discovery scans.
     - A cooperative-v1 provider must implement the explicit step contract and
       may yield between main-thread frames. Existing synchronous providers remain
       compatible, but serious non-cooperative overruns open the adapter circuit.
    - Adapter implementation and distribution belong to the integrating mod. Dev
      Bridge and participating mods are mutually optional; Dev Bridge packages
      contain no external adapter DLLs or manifests.

7. **In-game safety indicator**
   - A runtime-only nonblocking corner indicator is visible whenever transport is
     active or a write lease exists. Read-only, sandbox, and live-confirmed states
     have distinct labels/colors; live-confirmed access is forced visible even
     when the optional idle read-only display is disabled. Client count and lease
     context/expiry are shown in the compact details and tooltip.

8. **Macros, feature tests, captures, and reports**
   - Macros are declarative, cycle-checked, bounded, typed, and mode-derived.
   - Feature tests use phased execution and typed assertions, support `BLOCKED`,
     always attempt cleanup, and store queues/history under RimWorld user data.
     Existing XML suites migrate lazily.
   - Large state captures, diffs, screenshots, and reports are stored outside live
     memory and represented by safe paths plus hashes.

## Backward-compatibility framework

`DevTools/CompatibilityHarness` exercises protocol parsing, line serialization,
legacy command spelling, semantic-status mapping, byte limits, pagination,
authorization, deadlines, cancellation, idempotency, queue limits, macro mode
derivation, session invalidation, and manifest selection without launching the
game. Live acceptance then uses the existing `Send-RimWorldBridge.ps1` and current
mod adapters against an isolated save.

The replacement is introduced as a vertical slice before legacy code is removed:

1. Dormant bootstrap and activation.
2. Typed `STATUS` read.
3. Authorized idempotent `SET_SPEED` write.
4. One manifest-selected lazy adapter command.
5. One typed feature test.
6. Legacy PowerShell line-protocol request and response.

Only after all six pass may the superseded host path be deleted or disconnected.

## Completion gates

- Source verifier proves dormant bootstrap does not call provider, macro, test,
  hash, adapter, TCP timer, or map paths and has only the lightweight bootstrap
  update hook.
- Cold game measurement reports bootstrap, Harmony, and `Game.FinalizeInit`
  contributions; target is approximately 5 ms or less after assembly loading.
- Live probes cover activation, first/repeated command latency, bounded concurrent
  clients, queue expiry, disconnected writes, idempotent retry, old sessions,
  write leases, byte limits, pagination, adapter selection/reload, malformed
  manifests, lifecycle cleanup, safe no-map commands, and feature-test migration.
- No completion claim relies only on a green build or source substring checks.

## Implemented v2.1 evidence

- The Release compatibility harness reports its case count at runtime and covers
  protocol, boundary characterization, cursors, fair scheduling, cancellation,
  restart barriers, idempotency, leases, mutation confirmation, lifecycle,
  manifests, feature tests, batches, macros, transport, and production queries
   (the reported pass/fail count is authoritative for each build).
- A source invariant check confirms no dormant update work, AppDomain-wide provider
  scan, eager adapter load, macro parse, or feature-test parse.
- Final cold launches measured 16.329-21.568 ms construction/bootstrap including
  status publication, 1.022-1.474 ms in the single permanent Harmony patch, and
  0.094-0.122 ms in `FinalizeInit`. This is an honest miss of the approximately
  5 ms target. Approximate managed-heap deltas ranged from 1.9-3.0 MB while other
  mods were loading and are not claimed as bridge-only allocations.
- First fully loaded activation measured 219.415 ms internally and 880.446 ms at
  the external wake poll; this includes first-use Harmony detouring, JSON/JIT, and
  a 147.413 ms off-thread manifest pass. After idle, a second activation measured
  29.751 ms internally and 518.635 ms through the polling client; steady reindex
  measured 13.652 ms. A wake issued during mod loading can wait tens of seconds
  for RimWorld to resume root updates and is reported separately from bridge work.
- Raw loopback `STATUS` measured 35.288 ms for the first command and 5.813 ms
  repeated mean (1.845 ms minimum). The compatibility PowerShell process adds
  startup and script parsing overhead.
  - Before first adapter use, the historical bridge had four adapters and 116
    commands with zero retained hot generations. First hot-provider loads measured
     about 5 ms each. A live owner replacement selected the new manifest before
     load, retained the old Mono generation honestly, and loaded the new generation
     only when requested.
  - Seventy-seven unmanifested historical DLLs were then pruned. Current Dev
    Bridge distribution contains no owner adapter manifests or DLLs; owner mods
    package their own current `DevTools/BridgeAdapters` pair and are discovered
    only when the owner is loaded.
- An unfiltered 20,000-object `THINGS limit=5` query fell from 98.567 ms in the
  initial v2 implementation to 0.052-0.225 ms after JIT. Sixteen concurrent
  compatibility clients returned isolated successful IDs. Feature-test discovery
  measured 35.244 ms off-thread and 0.673 ms on the game thread.

## Maintainability pass metrics

The pre-refactor measurement was taken before moving any of the remaining
runtime, catalog, or diagnostics responsibilities. The after measurement is
the current source tree; generated assemblies are excluded.

| Composition root | Before | After | Change |
| --- | ---: | ---: | ---: |
| `BridgeRuntime.cs` | 1,266 lines | 862 lines | -404 (-31.9%) |
| `BridgeAdapterCatalog.cs` | 1,343 lines | 641 lines | -702 (-52.3%) |
| `BridgeDiagnostics.cs` | 1,225 lines | 779 lines | -446 (-36.4%) |
| Combined hotspots | 3,834 lines | 2,282 lines | -1,552 (-40.5%) |

The reduction is responsibility movement, not contract removal. The extracted
runtime services cover lifecycle dispatch, lease expiry, status/file output,
request preparation/execution, dormant activation, and the legacy file protocol.
The extracted adapter services cover source discovery, assembly verification,
manifest validation, generation management, lazy loading, and execution health.
The extracted diagnostics services cover command routing, cooperative snapshot
projection, artifact serialization, and the bounded event journal. The current
compatibility harness reports 69 cases, including the characterization tests
added before this pass for command registration, concurrent event access,
audit redaction, and concurrent catalog readers.

The principal migration risks are stale lifecycle callbacks, stale transport
generations, accidental owner-thread adoption, adapter assembly retention on
Mono, and accidental wire-format or secret-redaction changes. Regression and
concurrency evidence must therefore remain green before each logical commit;
no adapter or runtime extraction is allowed to alter stable status/error codes,
session rotation, lease invalidation, bounded queue behavior, or snapshot
consistency.
