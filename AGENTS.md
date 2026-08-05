# RimWorld Dev Bridge

- Package ID: `Lan.RimWorldDevBridge`.
- Generic adapter discovery scans a loaded owner's `DevTools/BridgeAdapters`; the legacy `DevTools/HotAdapters` source is development-only.
- Build: `dotnet build Source\RimWorldDevBridge\RimWorldDevBridge.csproj -c Release`.
- Coordinator build: `dotnet build DevTools\RestartCoordinator\RimWorldDevBridge.RestartCoordinator.csproj -c Release`.
- Full private build: `DevTools\Build-RimWorldDevBridge.ps1` after configuring private RimWorld/Unity/Harmony paths.
- Portable validation: `DevTools\Build-RimWorldDevBridge.ps1 -PortableOnly`.
- Validate: `DevTools\Test-BridgeSourceInvariants.ps1`, `DevTools\Package-RimWorldDevBridge.ps1 -Build`.
- Canonical client: `DevTools\devbridge.ps1`; `Send-RimWorldBridge.ps1` is a temporary delegating wrapper.
- Client output is JSON on stdout with diagnostics on stderr; use `--agent-id` only for an explicit override.
- Before runtime tests, query live context with `DevTools\devbridge.ps1 discover --json` and `context --package-id Lan.RimWorldDevBridge --json`.
- Adapter-only changes use `adapter publish` then `adapter reload`; gameplay assemblies, defs, Harmony patches, serialized types, or core changes require a full restart.
- Remote mutation is disabled by default. A client-supplied sandbox/live label is intent only; writes additionally require server-observed in-game confirmation for the current save and an agent-owned lease.
- Restart automation may control only an explicitly coordinator-owned sandbox; attached or live processes require a person or external orchestrator.
- Dev Bridge owns contracts, discovery, validation, execution, safety, and diagnostics. Integrations and adapter distribution remain owner-controlled; Dev Bridge is optional.
- Full workflow: `DevTools/DEVBRIDGE_AGENT.md`.
