# RimWorld Dev Bridge

RimWorld Dev Bridge is an optional, loopback-only developer bridge for RimWorld 1.6. It provides
bounded diagnostics, read-only queries, owner-mod adapter discovery, cooperative main-thread
execution, and explicit development automation. It does not include RimWorld, Unity, Harmony, or
owner-mod assemblies.

## Installation

Extract the release ZIP into the RimWorld `Mods` directory. Keep `About/About.xml` and
`LoadFolders.xml` unchanged. Harmony is a separate dependency declared by `About.xml`; it is not
redistributed by this repository. The bridge remains usable without participating owner mods, and
owner mods remain usable without Dev Bridge.

## Security

The transport is loopback-only and dormant until an explicit wake event. The remote-mutation
setting is disabled by default. A client label such as `sandbox` is intent, not proof. Mutation
requires the setting, visible in-game confirmation for the current game/save, an agent-owned
short-lived lease, authentication, validation, deadline, and idempotency checks. Confirmation warns
that remote tools may modify or destroy game state and can be revoked in-game. Tokens and lease
secrets are redacted by default.

The server records `gameIdentity` and `saveIdentity` separately. The former identifies the current
in-memory game for this process. The latter is a versioned SHA-256 digest of the server-observed
loaded-save value and is `none` for new or unsaved games. Raw save names/paths and other sensitive
metadata are never exposed; these identities are process/session-scoped, not portable save IDs.

Every connected game is live and non-disposable by default. Use read-only commands first. Only the
human operator may explicitly identify the current game as a disposable sandbox; never infer this
from a client label, command, naming convention, dev mode, or bridge state. The external restart
coordinator controls only explicitly owned sandbox processes. Attached or live processes require a
person or external orchestrator.

## Canonical Client

Run the client from this repository or from the packaged ZIP:

```powershell
pwsh -File .\DevTools\devbridge.ps1 help --json
pwsh -File .\DevTools\devbridge.ps1 discover --json
pwsh -File .\DevTools\devbridge.ps1 wake --json
pwsh -File .\DevTools\devbridge.ps1 context --package-id Lan.RimWorldDevBridge --json
pwsh -File .\DevTools\devbridge.ps1 describe --package-id Lan.RimWorldDevBridge --json
pwsh -File .\DevTools\devbridge.ps1 read --command STATUS --json
pwsh -File .\DevTools\devbridge.ps1 call SYNC --json
```

Path resolution priority is explicit argument, documented environment variable, local user
configuration, then bounded platform discovery. Use `--bridge-root` or
`RIMWORLD_DEVBRIDGE_BRIDGE_ROOT` when discovery is insufficient. Use `--agent-id` only when an
explicit identity override is required; otherwise the client persists an opaque identity under
the bridge user-data directory, never in the repository.

The client writes JSON to stdout and diagnostics to stderr. Typical exit codes are 0 for success,
2 for invalid request/client errors, 3 for path/configuration errors, 4 for unavailable or stale
bridge state, and 5 for failed post-restart context handshakes. `Send-RimWorldBridge.ps1` remains a
temporary compatibility wrapper and delegates to `devbridge.ps1`.

Lease and mutation examples:

```powershell
pwsh -File .\DevTools\devbridge.ps1 lease acquire --context=sandbox --json
pwsh -File .\DevTools\devbridge.ps1 lease inspect --json
pwsh -File .\DevTools\devbridge.ps1 lease renew --lease-token=<token> --json
pwsh -File .\DevTools\devbridge.ps1 lease release --lease-token=<token> --json
pwsh -File .\DevTools\devbridge.ps1 mutate --command=SET_SPEED --idempotency-key=<key> --json
pwsh -File .\DevTools\devbridge.ps1 cancel --request-id=<request-id> --json
```

Mutation commands generate and return an idempotency key when one is omitted. Supply the same key
only for an intentional retry after a fresh context and newly acquired authority.

## Adapters

Owner mods own adapter source, projects, manifests, generation-specific DLLs, tests, and package
validation. They publish current pairs under their own `DevTools/BridgeAdapters` directory. Dev
Bridge discovers those directories only for loaded owner mods, validates metadata off-thread, and
loads code lazily. `DevTools/HotAdapters` is a development/legacy override and is not in the
public package. Adapter-only changes can be published and reloaded; gameplay assemblies, defs,
Harmony patches, serialized types, and core changes require a full restart.

## Build and Test

Portable checks require only PowerShell, Bash, Git, and the checked-in source:

```powershell
pwsh -File .\DevTools\Build-RimWorldDevBridge.ps1 -PortableOnly
```

The full private build requires a local RimWorld 1.6 Managed directory and a compatible
`0Harmony.dll`. Configure them without committing paths:

```powershell
Copy-Item .\Build\devbridge.build.json.example .\Build\devbridge.build.json
pwsh -File .\DevTools\Build-RimWorldDevBridge.ps1
```

Edit only the untracked local configuration, or pass private paths directly. For example, the
configuration values are `rimWorldManagedDir` and `harmonyPath`; they must point to the local
`RimWorldWin64_Data/Managed` directory and `0Harmony.dll` without being committed.

Equivalent environment variables are `RIMWORLD_MANAGED_DIR` and `RIMWORLD_HARMONY_PATH`. The
entrypoint validates Assembly-CSharp, UnityEngine.CoreModule, and 0Harmony identities before
building. It then builds the core, coordinator, and harness, runs the harness and source
invariants, creates the release package, and runs package smoke validation.

The private build never downloads or uploads proprietary dependencies. Portable CI runs
`DevTools/Test-Portable.ps1` and Bash syntax checks; a self-hosted/private runner with locally
supplied paths is required for the full mod build.

## Packaging

```powershell
pwsh -File .\DevTools\Package-RimWorldDevBridge.ps1 -Build -OutputDirectory .\Release
pwsh -File .\DevTools\Test-RimWorldDevBridgePackageSmoke.ps1 `
  -ArtifactPath .\Release\RimWorldDevBridge-2.1.0.zip
```

The package verifier checks exact declared entries, raw ZIP paths, core identity and hash,
forbidden artifacts, manifest declarations, and absence of external adapters. Generated output is
ignored and must not be committed.

## Troubleshooting

`status_unavailable` or `bridge_not_active` means the process is absent, dormant, stale, or the
bridge root/user root is wrong. Supply explicit roots and run `wake`, then query fresh context.
`RimWorldManagedDir is not configured` or a missing assembly error means the private dependency
inputs are absent; do not copy those files into this repository. A fingerprint, boot, session, or
transport mismatch requires discarding cached context, leases, cursors, and handles.

## Limitations

RimWorld/Unity work remains serialized on RimWorld's main thread. Legacy synchronous adapters cannot
be preempted and are measured and circuit-broken after serious overruns. A component inside RimWorld
cannot survive process exit; restart coordination is external and cannot revive an ended Codex run.

See `BRIDGE_HANDOFF.md`, `AGENTS.md`, and `DevTools/DEVBRIDGE_AGENT.md` for the operational workflow.
