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
- Provider reload scans types from every loaded AppDomain assembly, initializes
  macros, enumerates and hashes every hot DLL, and loads each unseen DLL from
  bytes. The live session retained 80 hot generations: 60 Horticulture, 14
  Aquaculture, and 6 Flockmaster assemblies (4,676,096 source bytes total).
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
builds only when no RimWorld process is active, launches `-quicktest` or attaches to
an existing process, binds readiness to the observed process ID and disk manifest,
and invokes the normal authenticated client. It only stops a process it launched.

## Selected architecture

1. **Bootstrap and lifecycle**
   - The mod constructor records paths, installs one explicit game-change patch and
     one lightweight `Root.Update` signal/drain hook, creates one save-data wake
     watcher, registers process shutdown, and writes a fixed dormant status.
     `GameComponent.FinalizeInit` supplies game readiness.
   - Dormant mode performs no provider scan, adapter load, macro/test parse,
     fingerprint, map scan, timer, or TCP work; the permanent update hook only
     checks coalesced file signals and returns.
   - The `Root.Update` hook is installed during safe bootstrap, activates transport
     and main-thread draining only after a signal, and is removed at shutdown.
   - Every loaded game receives a random session ID. Game replacement/unload
     rotates authorization, cancels queued requests, and invalidates references.

2. **Transport and protocol boundary**
   - Loopback TCP starts only after a wake request and stops after bounded idle.
   - Requests are byte-bounded and parsed into `BridgeRequest`; responses are
     `BridgeResult` values with status, schema, timing, provider, mode, mutation,
     truncation, warnings, and structured fields.
   - Protocol 9 line requests remain accepted. Protocol 10 adds key/value request
     options and JSON output without changing the legacy PowerShell entry point.

3. **Main-thread scheduler**
   - Background clients enqueue into a bounded priority queue. Only one scheduled
     main-thread drain callback exists at a time.
   - Drain work has a per-frame time/operation budget. Expired, cancelled,
     disconnected, stale-session, unauthorized, or invalid requests are rejected
     before command execution.
    - Cost classes, operation/time drain budgets, bounded core scans, and one
      expensive operation per drain limit bridge-owned stalls. Deliberately
      expensive operations require an override. Built-in large scans use
      cooperative resumable steps. Adapters are cooperative only when they
      explicitly opt into the versioned contract; legacy synchronous code is
      measured, reported as non-cooperative, and cannot be safely preempted.

4. **Authorization and idempotency**
   - Sessions start read-only. Writes require an explicit short-lived lease bound
     to the session and a declared sandbox/live context. Remote mutation can be
     disabled in settings.
   - Command, macro, batch, and feature-test modes are derived transitively.
     Unknown commands are forbidden rather than implicitly safe.
   - Completed writes are cached by session plus idempotency key. A retry returns
     the original result. A bounded audit records every accepted mutation.

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
   - Sidecar manifests are indexed off-thread only after activation. The newest
     compatible manifest per stable adapter ID is selected without loading DLLs.
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

- The offline compatibility harness covers 28 protocol, bound, cursor, scheduler,
  cancellation, idempotency, manifest, feature-test, batch, and macro cases.
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
- Before first adapter use, four adapters and 116 commands were available with
  zero retained hot generations. First hot-provider loads measured about 5 ms
  each. A live Aquaculture replacement selected the new manifest before load,
  retained the old Mono generation honestly, and loaded the new generation only
  when requested.
- Seventy-seven unmanifested historical DLLs were then pruned. Final cold/live
  verification found five manifests, four logical adapters, one superseded
  generation, zero ignored DLLs, and zero retained hot assemblies before use.
- An unfiltered 20,000-object `THINGS limit=5` query fell from 98.567 ms in the
  initial v2 implementation to 0.052-0.225 ms after JIT. Sixteen concurrent
  compatibility clients returned isolated successful IDs. Feature-test discovery
  measured 35.244 ms off-thread and 0.673 ms on the game thread.
