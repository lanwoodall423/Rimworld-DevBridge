# Security Reporting

Report suspected vulnerabilities privately to the project owner rather than posting transport
tokens, lease tokens, save contents, paths, or process details in a public issue. Include the
commit, affected command or client operation, threat boundary, reproduction steps using a disposable
save, and whether the process was attached or coordinator-owned.

Important boundaries:

- The bridge is loopback-only and does not provide shell or arbitrary code execution.
- Every connected game is live and non-disposable by default. Only the human operator may explicitly
  identify the current game as a disposable sandbox; client labels and inference are not proof.
- Remote mutation is disabled by default and requires visible in-game confirmation plus an
  agent-owned lease.
- Audit `gameIdentity` and `saveIdentity` are separate server-observed values. `gameIdentity` is
  process-local; `saveIdentity` is a versioned digest of the loaded-save value or `none` for a new
  or unsaved game. Raw save names, paths, usernames, and credentials are never exposed.
- Tokens and secrets should be redacted from reports.
- Attached or live processes must never be restarted automatically.
- Proprietary RimWorld/Unity/Harmony files must not be attached to reports or commits.
