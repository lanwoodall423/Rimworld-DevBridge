# Optional Codex MCP Adapter

`RimWorldDevBridge.McpServer` is an optional external local adapter. It is a .NET 8
STDIO MCP server and never loads RimWorld, Unity, Harmony, or owner-mod assemblies.
It invokes the canonical `DevTools/devbridge.ps1` client and therefore preserves the
bridge protocol, coordinator ownership, authentication, authorization, leases,
confirmation, session rotation, and redaction boundaries.

The server's first instructions are safety-critical:

> bridge_not_active is recoverable for an authorized managed-test profile. Complete
> autonomous work before waiting for human review. Restart only coordinator-owned
> managed instances. Every connected game is live and non-disposable by default.
> Restart authorization is not mutation authorization. Never claim or terminate a
> manual/external process.

## Build

The maintained SDK is the official C# `ModelContextProtocol` package, version 2.1.0,
with `Microsoft.Extensions.Hosting` 8.0.1. The reproducible self-contained Windows
publish is:

```powershell
pwsh -File .\DevTools\Build-RimWorldDevBridgeMcpServer.ps1 `
  -OutputDirectory .\McpServerArtifact -RuntimeIdentifier win-x64
```

The output directory must be empty or absent. The script refuses to replace existing
files and emits the executable hash. The optional artifact contains only the
self-contained executable and `THIRD_PARTY_NOTICES.md`. It is not part of the strict
eleven-file RimWorld package.

For a framework-dependent local build, install the .NET 8 SDK and run:

```powershell
dotnet build .\DevTools\McpServer\RimWorldDevBridge.McpServer.csproj -c Release
```

STDIO stdout is reserved for MCP JSON-RPC. Diagnostics go to stderr and are sanitized.

## Configuration

The server accepts `--bridge-root`, `--user-root`, `--client`, `--powershell`, and
`--tool-timeout-ms`. The bridge and user roots must already exist. Configure the
canonical client explicitly when running from a published artifact.

The adapter is host-neutral MCP STDIO. OpenCode and other MCP hosts should register
the same executable and argument list in their own MCP configuration; no Codex runtime,
plugin, public endpoint, API key, or cloud service is required.

Register a published server with Codex:

```powershell
codex mcp add rimworld-devbridge -- `
  C:\Path\McpServerArtifact\RimWorldDevBridge.McpServer.exe `
  --bridge-root C:\Path\RimWorldDevBridge `
  --user-root C:\Path\RimWorldUserRoot `
  --client C:\Path\RimWorldDevBridge\DevTools\devbridge.ps1
```

Inspect registration and the server from Codex:

```powershell
codex mcp list
```

Use `/mcp` in the Codex TUI to inspect connection, tools, instructions, and approval
state. Remove or disable the registration with:

```powershell
codex mcp remove rimworld-devbridge
```

Project-scoped `.codex/config.toml` example:

```toml
[mcp_servers.rimworld_devbridge]
command = "C:\\Path\\McpServerArtifact\\RimWorldDevBridge.McpServer.exe"
args = [
  "--bridge-root", "C:\\Path\\RimWorldDevBridge",
  "--user-root", "C:\\Path\\RimWorldUserRoot",
  "--client", "C:\\Path\\RimWorldDevBridge\\DevTools\\devbridge.ps1"
]
cwd = "C:\\Path\\McpServerArtifact"
enabled = true
required = false
startup_timeout_sec = 10
tool_timeout_sec = 300
default_tools_approval_mode = "prompt"
enabled_tools = [
  "ensure_bridge_ready",
  "get_bridge_status",
  "get_fresh_context",
  "list_bridge_capabilities",
  "run_read_only_query",
  "validate_owner_adapter",
  "request_managed_restart",
  "wait_for_runtime",
  "list_human_reviews",
  "create_human_review",
  "resolve_human_review",
  "get_resume_checkpoint"
]

[mcp_servers.rimworld_devbridge.tools.get_bridge_status]
approval_mode = "auto"

[mcp_servers.rimworld_devbridge.tools.get_fresh_context]
approval_mode = "auto"

[mcp_servers.rimworld_devbridge.tools.list_bridge_capabilities]
approval_mode = "auto"

[mcp_servers.rimworld_devbridge.tools.run_read_only_query]
approval_mode = "prompt"

[mcp_servers.rimworld_devbridge.tools.ensure_bridge_ready]
approval_mode = "prompt"

[mcp_servers.rimworld_devbridge.tools.request_managed_restart]
approval_mode = "prompt"
```

The `auto` entries are bounded status/context reads only. Keep activation and restart
approval enabled because they can start or stop an external process. Do not set
`request_managed_restart`, mutation, destructive external, or attached-process control
to an approval-free mode. The server exposes no gameplay mutation tool.

### Authorized managed-test zero-touch profile

After `restart authorize-sandbox` has explicitly authorized the disposable managed-test
profile, a trusted project may opt into zero-touch **managed lifecycle recovery** for
`ensure_bridge_ready` while retaining its truthful `destructiveHint=true` annotation:

```toml
[mcp_servers.rimworld_devbridge.tools.ensure_bridge_ready]
approval_mode = "auto"
```

The server still requires the profile/hash/user-root authorization and coordinator
ownership beneath this Codex setting. It never claims an attached process, enables
gameplay mutation, confirms an in-game warning, or acquires a write lease. Keep
`request_managed_restart`, owner validation, human-review resolution, and every
destructive external operation at `prompt`; `get_bridge_status`, context reads, and
`wait_for_runtime` may be `auto` as bounded observations.

To disable without removing the entry, set `enabled = false`. To make startup a hard
dependency for a project, set `required = true` only when that project can operate
without a fallback client.

## Tools

The server exposes focused tools rather than every CLI flag:

| Tool | Operation | Safety |
| --- | --- | --- |
| `ensure_bridge_ready` | Authorized wake/ensure/retry/context refresh | External lifecycle; approval required |
| `get_bridge_status` | Discover with safe activation | Activation-capable; approval metadata is truthful |
| `get_fresh_context` | Fresh package context | Activation-capable |
| `list_bridge_capabilities` | Descriptor/capability discovery | Read-only external inspection |
| `run_read_only_query` | Explicit pure-read command allowlist | Rejects mutation and generic calls |
| `validate_owner_adapter` | Targeted owner validation | Open-world filesystem/process inspection |
| `request_managed_restart` | Coordinator-owned managed-test ensure | External lifecycle; approval required |
| `wait_for_runtime` | Wait for an existing ticket | Does not claim a process |
| `list_human_reviews` | List durable reviews | Read-only queue inspection |
| `create_human_review` | Persist human work | Does not authorize safety or mutation |
| `resolve_human_review` | Record a human answer | Does not authorize safety or mutation |
| `get_resume_checkpoint` | Retrieve resumable work | Read-only queue inspection |

Every result includes a correlation ID, stable Dev Bridge code, bounded data, and the
recovery fields `activationState`, `waitFor`, `recoverable`, `requiredAction`,
`keepRunning`, `retrySafe`, `operatorActionRequired`, and `nextAction` when applicable.
Canonical values are `bridge_ready` with phase `READY` and
`attached_live_process_requires_operator`. Review resolution never confirms an
in-game warning, grants a lease, or authorizes attached-process control.

`request_managed_restart` validates the requested bridge postcondition and current PID/session/
lifecycle-generation/build identity before accepting a coalesced cycle. A request requiring a new
process or assembly cannot succeed by joining a stale `WAITING_FOR_GAME` cycle. Stale cycles are
watchdogged and may be superseded only for an authorized coordinator-owned managed instance; waiting
callers follow the replacement cycle and receive fresh context before success.

## Verification

The protocol test starts the server over STDIO and verifies initialization instructions,
stdout JSON purity, tool discovery, schemas, annotations, malformed input, cancellation
notification handling, redaction, activation failure fields, review checkpoint/resume,
and mutation-bypass rejection:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\DevTools\Test-DevBridgeMcpServer.ps1 `
  -ServerDll .\DevTools\McpServer\bin\Release\net8.0\RimWorldDevBridge.McpServer.dll `
  -ClientPath .\DevTools\devbridge.ps1
```

MCP Inspector and a real local Codex registration are required for end-to-end release
evidence when those tools are installed. An unavailable Inspector, Codex registration,
or managed RimWorld instance must be reported as unrun, never as passed.
