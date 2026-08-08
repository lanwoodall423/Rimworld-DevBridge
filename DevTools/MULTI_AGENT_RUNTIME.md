# Multi-Agent Runtime Coordination

This document describes the coordination and isolation foundation. It is an
implementation contract, not an authorization grant.

## Identity

Each request carries separate values for `agentId`, `clientInstanceId`,
`connectionSessionId`, `correlationId`, and `participantId`. A logical agent may
create multiple client instances; one client instance may reconnect with a new
connection session; each request attempt has a new correlation; and each shared
operation membership has its own participant. Client-instance persistence is kept
outside the RimWorld user root so deleting or replacing a game profile does not
silently change the reconnect/quota subject.

Values are bounded and sanitized before diagnostics. They are not secrets and do
not prove identity. The canonical client also persists a separate client
credential outside the game user root; when supplied, the bridge binds that
credential to the agent/client tuple before coordinated work. Authentication,
in-game confirmation, write leases, managed process ownership, participant
checks, and quotas remain independent controls. Legacy requests without a
client credential retain transport authentication for compatibility and never
gain authority from identity labels alone.

## Shared Operations

The durable registry covers activation, restart, readiness, save/load, adapter
reload, deployment, and verification. Its compatibility key is canonical and
contains:

- operation kind and desired postcondition
- managed profile, RimWorld version, mod/load-order, and source/build identity
- deployment slot, core/adapter/assembly fingerprints, and configuration fingerprint
- user-root, save, and map targets
- process-replacement requirement, lifecycle generation, and mutation scope

Correlation, goal, and participant IDs are excluded. Equal keys join one operation;
any changed value creates incompatible work. Participants may join, observe, wait,
reconnect, detach, or cancel only their own membership. Final-detach behavior is
persisted per operation: safe verification can cancel, while lifecycle work may
leave a coordinator-owned process running. Human review never grants a participant
lease, mutation, or process authority.

Operation records persist `operationId`, `operationKind`, `operationState`,
`compatibilityKey`, `desiredState`, participant state, terminal/recovery/retry fields,
capacity, `keepRunning`, launch state, and progress. Recovery validates ownership,
PID, process-start identity, session, slot, lifecycle, and forward progress. A
recorded launch is never repeated merely because a coordinator restarted.
Durable reads and mutations take the state-path mutex before reloading and
persisting, so a second coordinator or slot manager cannot overwrite a newer
snapshot. Global, per-agent, and per-client active/queued quotas are reported as
structured capacity failures rather than silently bypassed.

## Artifacts and Deployment

An artifact fingerprint is derived from source revision and dirty state, build and
framework, dependencies, mod/load order, output hashes, assembly name/version/MVID/
hash, deployment slot, and provenance. A deployment manifest binds the exact artifact,
per-file hashes, assembly metadata, producing agent, and deployment operation.

Deployment takes a scoped durable lock, renews its expiry during staging and
publication, verifies every hash, atomically publishes the versioned directory
and current manifest, and then checks the loaded assembly fingerprint. Stale
locks are removed only after bounded ownership validation. Modified output,
path traversal, wrong-agent or wrong-operation provenance, stale locks, and
loaded-fingerprint mismatch fail closed.

## Managed Slots and Fair Capacity

A managed slot owns the validated profile, user/config/mod roots, deployment overlay,
coordinator state, process identity, IPC endpoint, save, log, evidence, resource,
and lifecycle paths. Compatible operations share a slot. Incompatible operations use
an isolated slot or enter a structured fair queue. The manager exposes global active
process capacity, per-agent/client queue order, `capacityState`, and `nextAction`.

Slots reject overlapping paths and never claim attached/manual processes. A
recovered process is valid only when PID, process-start identity, executable,
profile, session, lifecycle generation, slot, and loaded fingerprint match. This
prevents PID reuse and stale observation from becoming ownership. Cleanup is
bounded and releases locks, participant resources, and leases without stopping
an externally owned process. Coordinator state is scoped by runtime slot/profile
instead of using one shared coordinator root.

## Runtime Lane and Recovery

Verse/Unity access remains on the owner game thread. Lifecycle, deployment, save/load,
adapter reload, stateful setup, mutation, and cleanup use the fair serialized runtime
lane. Compatible pure reads may overlap within bounded scheduler capacity. Observations
with stale session, lifecycle, progress, or fingerprint identity are rejected.

Safe retries are idempotent and bounded: reconnect or join an existing compatible
operation before launching; detach only the current participant; retry only when
`retrySafe=true`; and follow `nextAction`. `bridge_not_active` is recoverable only
for an authorized managed-test profile. `attached_live_process_requires_operator`
requires a person or external orchestrator. A live externally owned process is never
replaced by a coordinator retry.
Coordinator bridge handshakes may additionally bind the expected PID,
process-start identity, bridge session, and lifecycle generation. A mismatch is
treated as stale ownership, not as permission to attach to the observed process.

## Canonical Evidence

Record the exact client command, agent/client/participant IDs, operation and
compatibility key, slot/deployment/artifact hashes, PID and process-start identity,
session, lifecycle/progress sequence, capacity state, terminal/recoverable/retry-safe
values, and `nextAction`/`keepRunning`. Mark each result `real`, `simulated`,
`unavailable`, or `blocked`; do not convert an unavailable managed instance or MCP
Inspector into a passing runtime result.
