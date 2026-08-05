# Security Reporting

Report suspected vulnerabilities privately to the project owner rather than posting transport
tokens, lease tokens, save contents, paths, or process details in a public issue. Include the
commit, affected command or client operation, threat boundary, reproduction steps using a disposable
save, and whether the process was attached or coordinator-owned.

Important boundaries:

- The bridge is loopback-only and does not provide shell or arbitrary code execution.
- Remote mutation is disabled by default and requires visible in-game confirmation plus an
  agent-owned lease.
- Tokens and secrets should be redacted from reports.
- Attached or live processes must never be restarted automatically.
- Proprietary RimWorld/Unity/Harmony files must not be attached to reports or commits.
