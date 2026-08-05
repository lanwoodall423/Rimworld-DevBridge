# Contributing

Keep changes focused on runtime safety, compatibility, diagnostics, discovery, or portable tooling.
Do not commit RimWorld, Unity, Harmony, owner-mod, build, package, or local user-data artifacts.

Use `DevTools/Build-RimWorldDevBridge.ps1 -PortableOnly` for changes that do not require private
game assemblies. For a full local validation, supply `RIMWORLD_MANAGED_DIR` and
`RIMWORLD_HARMONY_PATH` or pass the equivalent build parameters. Do not put those paths in project
files or commits.

Before submitting a change, run the portable checks, the compatibility harness when private
dependencies are available, source invariants, and package verification. Preserve protocol
compatibility unless a change is explicitly documented. Runtime behavior must remain main-thread
safe and read-only by default.

Do not add a license file through a contribution without an explicit project-owner decision.
