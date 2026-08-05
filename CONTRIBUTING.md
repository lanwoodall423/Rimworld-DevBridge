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

This repository is released under the MIT License in `LICENSE`. Preserve that notice in source
distributions and packages. Do not add third-party license text or imply that the MIT License covers
RimWorld, Unity, Harmony, or participating mod content; retain their upstream notices and obtain
their terms separately.
