using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace RimWorldDevBridge
{
    [DataContract]
    public sealed class BridgeOperationLimits
    {
        [DataMember(Order = 1)] public int MaximumActiveOperations = 1;
        [DataMember(Order = 2)] public int MaximumQueuedOperations = 64;
        [DataMember(Order = 3)] public int MaximumActivePerAgent = 2;
        [DataMember(Order = 4)] public int MaximumQueuedPerAgent = 16;
        [DataMember(Order = 5)] public int MaximumParticipantsPerOperation = 32;
        [DataMember(Order = 6)] public int MaximumActivePerClient = 2;
        [DataMember(Order = 7)] public int MaximumQueuedPerClient = 16;

        internal BridgeOperationLimits Bounded()
        {
            return new BridgeOperationLimits
            {
                MaximumActiveOperations = Math.Max(1, Math.Min(64, MaximumActiveOperations)),
                MaximumQueuedOperations = Math.Max(1, Math.Min(1024, MaximumQueuedOperations)),
                MaximumActivePerAgent = Math.Max(1, Math.Min(64, MaximumActivePerAgent)),
                MaximumQueuedPerAgent = Math.Max(1, Math.Min(256, MaximumQueuedPerAgent)),
                MaximumParticipantsPerOperation = Math.Max(1, Math.Min(256, MaximumParticipantsPerOperation)),
                MaximumActivePerClient = Math.Max(1, Math.Min(64, MaximumActivePerClient)),
                MaximumQueuedPerClient = Math.Max(1, Math.Min(256, MaximumQueuedPerClient))
            };
        }
    }

    [DataContract]
    public sealed class BridgeOperationParticipantRecord
    {
        [DataMember(Order = 1)] public string ParticipantId;
        [DataMember(Order = 2)] public string AgentId;
        [DataMember(Order = 3)] public string ClientInstanceId;
        [DataMember(Order = 4)] public string ConnectionSessionId;
        [DataMember(Order = 5)] public string RequestCorrelationId;
        [DataMember(Order = 6)] public BridgeParticipationState State;
        [DataMember(Order = 7)] public DateTime JoinedAtUtc;
        [DataMember(Order = 8)] public DateTime LastSeenAtUtc;
        [DataMember(Order = 9)] public long LastObservedProgressSequence;
        [DataMember(Order = 10)] public string SanitizedAgentId;
        [DataMember(Order = 11)] public string SanitizedClientInstanceId;
    }

    [DataContract]
    public sealed class BridgeOperationRecord
    {
        [DataMember(Order = 1)] public string OperationId;
        [DataMember(Order = 2)] public BridgeOperationKind OperationKind;
        [DataMember(Order = 3)] public BridgeOperationState OperationState;
        [DataMember(Order = 4)] public string CompatibilityKey;
        [DataMember(Order = 5)] public BridgeDesiredState DesiredState;
        [DataMember(Order = 6)] public string RuntimeSlotId;
        [DataMember(Order = 7)] public string DeploymentId;
        [DataMember(Order = 8)] public string ArtifactFingerprint;
        [DataMember(Order = 9)] public string LoadedAssemblyFingerprint;
        [DataMember(Order = 10)] public int Pid;
        [DataMember(Order = 11)] public string ProcessStartIdentity;
        [DataMember(Order = 12)] public string SessionId;
        [DataMember(Order = 13)] public long LifecycleGeneration;
        [DataMember(Order = 14)] public long ProgressSequence;
        [DataMember(Order = 15)] public DateTime LastProgressAtUtc;
        [DataMember(Order = 16)] public bool Terminal;
        [DataMember(Order = 17)] public bool Recoverable;
        [DataMember(Order = 18)] public bool RetrySafe;
        [DataMember(Order = 19)] public string NextAction;
        [DataMember(Order = 20)] public string CapacityState;
        [DataMember(Order = 21)] public bool KeepRunning;
        [DataMember(Order = 22)] public BridgeAbandonmentPolicy AbandonmentPolicy;
        [DataMember(Order = 23)] public bool LaunchIssued;
        [DataMember(Order = 24)] public bool CoordinatorRestarted;
        [DataMember(Order = 25)] public string FailureCode;
        [DataMember(Order = 26)] public string RequestedGoalId;
        [DataMember(Order = 27)] public long QueueSequence;
        [DataMember(Order = 28)] public DateTime CreatedAtUtc;
        [DataMember(Order = 29)] public DateTime UpdatedAtUtc;
        [DataMember(Order = 30)] public List<string> CallerGoalIds = new List<string>();
        [DataMember(Order = 31)] public List<BridgeOperationParticipantRecord> Participants =
            new List<BridgeOperationParticipantRecord>();
        [DataMember(Order = 32)] public int Version = 2;
        [DataMember(Order = 33)] public string RequestedWorkflow;
        [DataMember(Order = 34)] public string CurrentPhase;
        [DataMember(Order = 35)] public List<string> CompletedPhases = new List<string>();
        [DataMember(Order = 36)] public DateTime DeadlineUtc;
        [DataMember(Order = 37)] public DateTime ProgressDeadlineUtc;
        [DataMember(Order = 38)] public string AuthorizationReference;
        [DataMember(Order = 39)] public string TerminalResultCode;
        [DataMember(Order = 40)] public string TerminalResultDetail;
        [DataMember(Order = 41)] public string CleanupStatus;

        public int ActiveParticipantCount => Participants.Count(item => item.State == BridgeParticipationState.Attached);
    }

    [DataContract]
    internal sealed class BridgeSharedOperationState
    {
        [DataMember(Order = 1)] public int SchemaVersion = 2;
        [DataMember(Order = 2)] public long Sequence;
        [DataMember(Order = 3)] public long QueueSequence;
        [DataMember(Order = 4)] public List<BridgeOperationRecord> Operations =
            new List<BridgeOperationRecord>();
        [DataMember(Order = 5)] public string LastAdmittedAgentId;
    }

    public sealed class BridgeOperationJoinRequest
    {
        public BridgeClientIdentity Identity;
        public string OperationId;
        public BridgeOperationKind OperationKind;
        public BridgeDesiredState DesiredState;
        public BridgeOperationCompatibilityKey Compatibility;
        public string GoalId;
        public string RuntimeSlotId;
        public string DeploymentId;
        public string ArtifactFingerprint;
        public string LoadedAssemblyFingerprint;
        public bool KeepRunning;
        public string RequestedWorkflow;
        public DateTime DeadlineUtc;
        public DateTime ProgressDeadlineUtc;
        public string AuthorizationReference;
    }

    internal static class BridgeOperationStateMachine
    {
        internal static bool IsTerminal(BridgeOperationState state)
        {
            return state == BridgeOperationState.Succeeded || state == BridgeOperationState.Failed ||
                state == BridgeOperationState.Denied || state == BridgeOperationState.Cancelled ||
                state == BridgeOperationState.TimedOut || state == BridgeOperationState.Abandoned;
        }

        internal static void Transition(BridgeOperationRecord operation, BridgeOperationState next,
            DateTime nowUtc, string phase, string resultCode = null, string resultDetail = null)
        {
            if (operation == null) throw new InvalidOperationException("operation_not_found");
            BridgeOperationState current = operation.OperationState;
            if (current == next)
            {
                if (!string.IsNullOrWhiteSpace(phase)) operation.CurrentPhase = Bound(phase, 64);
                return;
            }
            if (IsTerminal(current)) throw new InvalidOperationException("operation_terminal");
            if (!CanTransition(current, next))
                throw new InvalidOperationException("operation_transition_invalid");
            if (!string.IsNullOrWhiteSpace(operation.CurrentPhase) &&
                (operation.CompletedPhases == null || !operation.CompletedPhases.Contains(operation.CurrentPhase)))
            {
                if (operation.CompletedPhases == null) operation.CompletedPhases = new List<string>();
                operation.CompletedPhases.Add(Bound(operation.CurrentPhase, 64));
            }
            operation.OperationState = next;
            operation.CurrentPhase = Bound(phase ?? next.ToString().ToLowerInvariant(), 64);
            operation.UpdatedAtUtc = nowUtc;
            if (IsTerminal(next))
            {
                operation.Terminal = true;
                operation.TerminalResultCode = Bound(resultCode, 128);
                operation.TerminalResultDetail = Bound(resultDetail, 512);
                operation.CleanupStatus = string.Equals(next.ToString(), "Succeeded", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(next.ToString(), "Failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(next.ToString(), "Denied", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(next.ToString(), "TimedOut", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(next.ToString(), "Cancelled", StringComparison.OrdinalIgnoreCase) ?
                    "pending" : "complete";
            }
        }

        internal static bool CanTransition(BridgeOperationState current, BridgeOperationState next)
        {
            if (current == next) return true;
            switch (current)
            {
                case BridgeOperationState.Pending:
                    return next == BridgeOperationState.Queued || next == BridgeOperationState.WaitingForAuthorization ||
                        next == BridgeOperationState.WaitingForCapacity || next == BridgeOperationState.Denied ||
                        next == BridgeOperationState.Failed || next == BridgeOperationState.Cancelled ||
                        next == BridgeOperationState.TimedOut || next == BridgeOperationState.Abandoned;
                case BridgeOperationState.Queued:
                    return next == BridgeOperationState.Running || next == BridgeOperationState.WaitingForCapacity ||
                        next == BridgeOperationState.WaitingForAuthorization || next == BridgeOperationState.Denied ||
                        next == BridgeOperationState.Failed || next == BridgeOperationState.Cancelled ||
                        next == BridgeOperationState.TimedOut || next == BridgeOperationState.Abandoned;
                case BridgeOperationState.WaitingForAuthorization:
                    return next == BridgeOperationState.Pending || next == BridgeOperationState.Queued ||
                        next == BridgeOperationState.Running || next == BridgeOperationState.Denied ||
                        next == BridgeOperationState.Failed || next == BridgeOperationState.Cancelled ||
                        next == BridgeOperationState.TimedOut || next == BridgeOperationState.Abandoned;
                case BridgeOperationState.WaitingForCapacity:
                    return next == BridgeOperationState.Queued || next == BridgeOperationState.Running ||
                        next == BridgeOperationState.WaitingForAuthorization ||
                        next == BridgeOperationState.Denied || next == BridgeOperationState.Failed ||
                        next == BridgeOperationState.Cancelled || next == BridgeOperationState.TimedOut ||
                        next == BridgeOperationState.Abandoned;
                case BridgeOperationState.Running:
                    return next == BridgeOperationState.WaitingForAuthorization ||
                        next == BridgeOperationState.Recovering || next == BridgeOperationState.Succeeded ||
                        next == BridgeOperationState.Failed || next == BridgeOperationState.Denied ||
                        next == BridgeOperationState.Cancelled || next == BridgeOperationState.TimedOut ||
                        next == BridgeOperationState.Abandoned;
                case BridgeOperationState.Recovering:
                    return next == BridgeOperationState.Queued || next == BridgeOperationState.Running ||
                        next == BridgeOperationState.Succeeded || next == BridgeOperationState.Failed ||
                        next == BridgeOperationState.Denied || next == BridgeOperationState.Cancelled ||
                        next == BridgeOperationState.TimedOut || next == BridgeOperationState.Abandoned;
                default:
                    return false;
            }
        }

        private static string Bound(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }

    [DataContract]
    public sealed class BridgeOperationJoinResult
    {
        [DataMember(Order = 1)] public bool Joined;
        [DataMember(Order = 2)] public bool Created;
        [DataMember(Order = 3)] public string OperationId;
        [DataMember(Order = 4)] public string ParticipantId;
        [DataMember(Order = 5)] public BridgeOperationRecord Operation;
        [DataMember(Order = 6)] public string CapacityState;
        [DataMember(Order = 7)] public bool Terminal;
        [DataMember(Order = 8)] public bool Recoverable;
        [DataMember(Order = 9)] public bool RetrySafe;
        [DataMember(Order = 10)] public string NextAction;
    }

    public sealed class BridgeSharedOperationCoordinator
    {
        private readonly object gate = new object();
        private readonly IBridgeClock clock;
        private readonly BridgeOperationLimits limits;
        private readonly string statePath;
        private BridgeSharedOperationState state;
        private readonly Dictionary<string, BridgeOperationRecord> byId =
            new Dictionary<string, BridgeOperationRecord>(StringComparer.Ordinal);

        public BridgeSharedOperationCoordinator(IBridgeClock clock = null, string statePath = null,
            BridgeOperationLimits limits = null)
        {
            this.clock = clock ?? new BridgeSystemClock();
            this.statePath = statePath;
            this.limits = (limits ?? new BridgeOperationLimits()).Bounded();
            state = string.IsNullOrWhiteSpace(statePath) ? new BridgeSharedOperationState() :
                BridgeDurableJson.Read<BridgeSharedOperationState>(statePath) ?? new BridgeSharedOperationState();
            if (state.Operations == null) state.Operations = new List<BridgeOperationRecord>();
            foreach (BridgeOperationRecord operation in state.Operations)
            {
                Normalize(operation);
                byId[operation.OperationId] = operation;
            }
        }

        public BridgeOperationLimits Limits => limits;

        public BridgeOperationJoinResult Join(BridgeOperationJoinRequest request)
        {
            if (request == null || request.Identity == null || request.Compatibility == null)
                throw new ArgumentException("operation_join_request_invalid");
            ValidateIdentity(request.Identity);
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                    AdvanceDeadlinesLocked();
                BridgeOperationRecord operation = state.Operations.LastOrDefault(item =>
                    !item.Terminal && string.Equals(item.CompatibilityKey, request.Compatibility.ToString(),
                        StringComparison.Ordinal));
                bool created = false;
                if (operation == null)
                {
                    if (QueuedCountLocked() >= limits.MaximumQueuedOperations)
                        return CapacityResult(null, request.Identity.ParticipantId, "global_queue_limit",
                            "wait for capacity and retry");
                    if (QueuedCountForAgentLocked(request.Identity.AgentId) >= limits.MaximumQueuedPerAgent)
                        return CapacityResult(null, request.Identity.ParticipantId, "agent_queue_limit",
                            "wait for this agent's fair capacity and retry");
                    if (QueuedCountForClientLocked(request.Identity.AgentId, request.Identity.ClientInstanceId) >=
                        limits.MaximumQueuedPerClient)
                        return CapacityResult(null, request.Identity.ParticipantId, "client_queue_limit",
                            "wait for this client instance's fair capacity and retry");
                    operation = NewOperation(request);
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Queued,
                        clock.UtcNow, "capacity");
                    state.Operations.Add(operation);
                    byId.Add(operation.OperationId, operation);
                    created = true;
                }
                else
                {
                    string quotaFailure = ParticipantQuotaFailureLocked(operation, request.Identity);
                    if (!string.IsNullOrEmpty(quotaFailure))
                        return CapacityResult(operation, request.Identity.ParticipantId, quotaFailure,
                            "wait for this client instance's quota and retry");
                }
                BridgeOperationParticipantRecord participant = operation.Participants.FirstOrDefault(item =>
                    string.Equals(item.ParticipantId, request.Identity.ParticipantId, StringComparison.Ordinal));
                if (participant != null)
                {
                    if (!string.Equals(participant.AgentId, request.Identity.AgentId, StringComparison.Ordinal) ||
                        !string.Equals(participant.ClientInstanceId, request.Identity.ClientInstanceId,
                            StringComparison.Ordinal))
                        return FailureResult(operation, request.Identity.ParticipantId, "participant_identity_mismatch",
                            false);
                    participant.State = BridgeParticipationState.Attached;
                    participant.ConnectionSessionId = request.Identity.ConnectionSessionId;
                    participant.RequestCorrelationId = request.Identity.RequestCorrelationId;
                    participant.LastSeenAtUtc = clock.UtcNow;
                }
                else
                {
                    if (operation.Participants.Count >= limits.MaximumParticipantsPerOperation)
                        return FailureResult(operation, request.Identity.ParticipantId,
                            "operation_participant_limit", true);
                    participant = NewParticipant(request.Identity);
                    operation.Participants.Add(participant);
                }
                if (!string.IsNullOrWhiteSpace(request.GoalId) && !operation.CallerGoalIds.Contains(request.GoalId))
                    operation.CallerGoalIds.Add(request.GoalId);
                if (created || operation.OperationState == BridgeOperationState.Queued ||
                    operation.OperationState == BridgeOperationState.WaitingForCapacity)
                    StartAvailableLocked();
                TouchLocked(operation);
                PersistLocked();
                return Result(operation, participant.ParticipantId, created);
                }
            }
        }

        public BridgeOperationRecord BindRuntimeSlot(string operationId, BridgeClientIdentity identity,
            string runtimeSlotId, string deploymentId = null, string artifactFingerprint = null)
        {
            if (identity == null || string.IsNullOrWhiteSpace(runtimeSlotId))
                throw new ArgumentException("runtime_slot_required");
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                RequireOwnedParticipant(operation, identity, false);
                if (!string.IsNullOrWhiteSpace(operation.RuntimeSlotId) &&
                    !string.Equals(operation.RuntimeSlotId, runtimeSlotId, StringComparison.Ordinal))
                    throw new InvalidOperationException("operation_runtime_slot_mismatch");
                operation.RuntimeSlotId = runtimeSlotId;
                if (!string.IsNullOrWhiteSpace(deploymentId)) operation.DeploymentId = deploymentId;
                if (!string.IsNullOrWhiteSpace(artifactFingerprint))
                    operation.ArtifactFingerprint = artifactFingerprint;
                operation.CapacityState = "admitted";
                operation.NextAction = "retry the original request to execute admitted work in the managed runtime slot";
                if (operation.OperationState != BridgeOperationState.Running)
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Running,
                        clock.UtcNow, "runtime");
                operation.Recoverable = false;
                operation.RetrySafe = false;
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
                }
            }
        }

        public BridgeOperationRecord SetCapacity(string operationId, BridgeClientIdentity identity,
            string capacityState, string nextAction, bool queued)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                RequireOwnedParticipant(operation, identity, false);
                if (operation.Terminal) return Clone(operation);
                operation.CapacityState = Bound(capacityState, 64);
                operation.NextAction = Bound(nextAction, 256);
                operation.Recoverable = queued;
                operation.RetrySafe = queued;
                if (queued)
                {
                    if (operation.OperationState == BridgeOperationState.Running)
                        BridgeOperationStateMachine.Transition(operation, BridgeOperationState.WaitingForCapacity,
                            clock.UtcNow, "capacity");
                    else if (operation.OperationState != BridgeOperationState.WaitingForCapacity)
                        BridgeOperationStateMachine.Transition(operation, BridgeOperationState.WaitingForCapacity,
                            clock.UtcNow, "capacity");
                }
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
                }
            }
        }

        public BridgeOperationRecord Observe(string operationId, BridgeClientIdentity identity)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                AdvanceDeadlinesLocked();
                BridgeOperationRecord operation = Find(operationId);
                BridgeOperationParticipantRecord participant = RequireOwnedParticipant(operation, identity, true);
                participant.LastSeenAtUtc = clock.UtcNow;
                participant.LastObservedProgressSequence = operation.ProgressSequence;
                PersistLocked();
                return Clone(operation);
                }
            }
        }

        public BridgeOperationRecord Reconnect(string operationId, BridgeClientIdentity identity)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                AdvanceDeadlinesLocked();
                BridgeOperationRecord operation = Find(operationId);
                BridgeOperationParticipantRecord participant = RequireOwnedParticipant(operation, identity, true);
                string quotaFailure = ParticipantQuotaFailureLocked(operation, identity);
                if (!string.IsNullOrEmpty(quotaFailure))
                    throw new InvalidOperationException(quotaFailure);
                participant.State = BridgeParticipationState.Attached;
                participant.ConnectionSessionId = identity.ConnectionSessionId;
                participant.RequestCorrelationId = identity.RequestCorrelationId;
                participant.LastSeenAtUtc = clock.UtcNow;
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
                }
            }
        }

        public BridgeOperationRecord Detach(string operationId, BridgeClientIdentity identity)
        {
            return EndParticipation(operationId, identity, BridgeParticipationState.Detached);
        }

        public BridgeOperationRecord CancelParticipation(string operationId, BridgeClientIdentity identity)
        {
            return EndParticipation(operationId, identity, BridgeParticipationState.Cancelled);
        }

        public BridgeOperationRecord Progress(string operationId, BridgeClientIdentity identity, long sequence,
            string nextAction = null)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                BridgeOperationParticipantRecord participant = RequireOwnedParticipant(operation, identity, false);
                if (operation.Terminal) return Clone(operation);
                if (sequence <= operation.ProgressSequence) return Clone(operation);
                operation.ProgressSequence = sequence;
                operation.LastProgressAtUtc = clock.UtcNow;
                if (operation.ProgressDeadlineUtc != default(DateTime))
                {
                    TimeSpan progressWindow = operation.ProgressDeadlineUtc - operation.LastProgressAtUtc;
                    if (progressWindow <= TimeSpan.Zero) progressWindow = TimeSpan.FromSeconds(1);
                    operation.ProgressDeadlineUtc = clock.UtcNow.Add(progressWindow);
                }
                operation.NextAction = nextAction ?? operation.NextAction;
                participant.LastObservedProgressSequence = sequence;
                participant.LastSeenAtUtc = clock.UtcNow;
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
                }
            }
        }

        public BridgeOperationRecord MarkLaunchIssued(string operationId, BridgeClientIdentity identity,
            BridgeProcessIdentity process = null)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                BridgeOperationParticipantRecord participant = RequireOwnedParticipant(operation, identity, false);
                if (operation.LaunchIssued && process != null && !SameProcess(operation, process))
                    throw new InvalidOperationException("operation_process_identity_mismatch");
                if (!string.IsNullOrEmpty(operation.LoadedAssemblyFingerprint) && process != null &&
                    !string.Equals(operation.LoadedAssemblyFingerprint, process.LoadedAssemblyFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("operation_loaded_fingerprint_mismatch");
                operation.LaunchIssued = true;
                if (process != null)
                {
                    operation.Pid = process.Pid;
                    operation.ProcessStartIdentity = process.ProcessStartIdentity;
                    operation.SessionId = process.SessionId;
                    operation.LifecycleGeneration = process.LifecycleGeneration;
                    operation.RuntimeSlotId = process.RuntimeSlotId;
                    operation.LoadedAssemblyFingerprint = process.LoadedAssemblyFingerprint;
                }
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
                }
            }
        }

        public BridgeOperationRecord Complete(string operationId, BridgeClientIdentity identity,
            string loadedAssemblyFingerprint = null, string failureCode = null)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                RequireOwnedParticipant(operation, identity, false);
                if (operation.Terminal) return Clone(operation);
                if (!string.IsNullOrEmpty(operation.LoadedAssemblyFingerprint) &&
                    !string.Equals(operation.LoadedAssemblyFingerprint, loadedAssemblyFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("operation_loaded_fingerprint_mismatch");
                if (!string.IsNullOrEmpty(loadedAssemblyFingerprint))
                    operation.LoadedAssemblyFingerprint = loadedAssemblyFingerprint;
                BridgeOperationState next = string.IsNullOrEmpty(failureCode) ? BridgeOperationState.Succeeded :
                    BridgeOperationState.Failed;
                BridgeOperationStateMachine.Transition(operation, next, clock.UtcNow, "cleanup",
                    failureCode, failureCode);
                operation.FailureCode = failureCode;
                operation.Recoverable = !string.IsNullOrEmpty(failureCode);
                operation.RetrySafe = operation.Recoverable;
                operation.NextAction = operation.Recoverable ? "inspect operation evidence and retry" : "none";
                TouchLocked(operation);
                StartAvailableLocked();
                PersistLocked();
                return Clone(operation);
                }
            }
        }

        public BridgeOperationRecord MarkCleanup(string operationId, BridgeClientIdentity identity,
            bool succeeded, string detail = null)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            using (BridgeDurableJson.AcquireStateLock(statePath))
            {
                ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                RequireOwnedParticipant(operation, identity, true);
                if (!operation.Terminal) throw new InvalidOperationException("operation_not_terminal");
                operation.CleanupStatus = succeeded ? "complete" : "failed";
                if (!succeeded)
                {
                    operation.Recoverable = true;
                    operation.RetrySafe = true;
                    operation.NextAction = "retry cleanup after inspecting the retained runtime state";
                }
                if (!string.IsNullOrWhiteSpace(detail)) operation.TerminalResultDetail = Bound(detail, 512);
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
            }
        }

        public BridgeOperationRecord WaitForAuthorization(string operationId, BridgeClientIdentity identity,
            string authorizationReference)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            using (BridgeDurableJson.AcquireStateLock(statePath))
            {
                ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                RequireOwnedParticipant(operation, identity, false);
                if (operation.Terminal) return Clone(operation);
                if (operation.OperationState != BridgeOperationState.WaitingForAuthorization)
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.WaitingForAuthorization,
                        clock.UtcNow, "authorization");
                operation.AuthorizationReference = Bound(authorizationReference, 256);
                operation.Recoverable = true;
                operation.RetrySafe = true;
                operation.NextAction = "provide the required authorization and retry";
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
            }
        }

        public BridgeOperationRecord Deny(string operationId, BridgeClientIdentity identity,
            string code = "authorization_denied", string detail = null)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            using (BridgeDurableJson.AcquireStateLock(statePath))
            {
                ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                RequireOwnedParticipant(operation, identity, false);
                if (!operation.Terminal)
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Denied,
                        clock.UtcNow, "cleanup", Bound(code, 128), Bound(detail, 512));
                operation.FailureCode = Bound(code, 128);
                operation.Recoverable = false;
                operation.RetrySafe = false;
                operation.NextAction = "none";
                TouchLocked(operation);
                StartAvailableLocked();
                PersistLocked();
                return Clone(operation);
            }
        }

        public int AdvanceDeadlines()
        {
            lock (gate)
            using (BridgeDurableJson.AcquireStateLock(statePath))
            {
                ReloadLocked();
                int changed = AdvanceDeadlinesLocked();
                if (changed > 0) PersistLocked();
                return changed;
            }
        }

        public BridgeOperationRecord RecoverAfterCoordinatorRestart(
            Func<BridgeOperationRecord, bool> ownershipStillValid = null)
        {
            lock (gate)
            {
                using (BridgeDurableJson.AcquireStateLock(statePath))
                {
                    ReloadLocked();
                BridgeOperationRecord last = null;
                foreach (BridgeOperationRecord operation in state.Operations.Where(item => !item.Terminal))
                {
                    operation.CoordinatorRestarted = true;
                    bool valid = ownershipStillValid == null || ownershipStillValid(Clone(operation));
                    if (!valid && operation.LaunchIssued)
                    {
                        BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Failed,
                            clock.UtcNow, "cleanup", "stale_operation_ownership",
                            "persisted process ownership no longer matches");
                        operation.Recoverable = true;
                        operation.RetrySafe = true;
                        operation.FailureCode = "stale_operation_ownership";
                        operation.NextAction = "reconcile PID, process start identity, session, and slot before retrying";
                    }
                    else if (operation.OperationState == BridgeOperationState.Queued ||
                        operation.OperationState == BridgeOperationState.Pending ||
                        operation.OperationState == BridgeOperationState.WaitingForCapacity ||
                        operation.OperationState == BridgeOperationState.WaitingForAuthorization)
                    {
                        operation.Recoverable = true;
                        operation.RetrySafe = true;
                        operation.NextAction = operation.OperationState == BridgeOperationState.WaitingForAuthorization ?
                            "obtain the required authorization before retrying" :
                            "wait for fair capacity scheduling";
                    }
                    else
                    {
                        if (operation.OperationState != BridgeOperationState.Recovering)
                            BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Recovering,
                                clock.UtcNow, "recovery");
                        operation.Recoverable = true;
                        operation.RetrySafe = true;
                        operation.NextAction = "reconnect or detach the persisted participants";
                    }
                    TouchLocked(operation);
                    last = operation;
                }
                StartAvailableLocked();
                PersistLocked();
                return last == null ? null : Clone(last);
                }
            }
        }

        public List<BridgeOperationRecord> Snapshot()
        {
            lock (gate)
            using (BridgeDurableJson.AcquireStateLock(statePath))
            {
                ReloadLocked();
                bool changed = AdvanceDeadlinesLocked() > 0;
                if (changed) PersistLocked();
                return state.Operations.Select(Clone).ToList();
            }
        }

        public BridgeClientIdentity ParticipantIdentity(string operationId)
        {
            lock (gate)
            using (BridgeDurableJson.AcquireStateLock(statePath))
            {
                ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                BridgeOperationParticipantRecord participant = operation.Participants.FirstOrDefault(item =>
                    item.State == BridgeParticipationState.Attached) ?? operation.Participants.FirstOrDefault();
                return participant == null ? null : BridgeClientIdentity.Create(participant.AgentId,
                    participant.ClientInstanceId, participant.ConnectionSessionId,
                    participant.RequestCorrelationId, participant.ParticipantId);
            }
        }

        private BridgeOperationRecord EndParticipation(string operationId, BridgeClientIdentity identity,
            BridgeParticipationState endState)
        {
            if (identity == null) throw new ArgumentException("identity_required");
            lock (gate)
            using (BridgeDurableJson.AcquireStateLock(statePath))
            {
                ReloadLocked();
                BridgeOperationRecord operation = Find(operationId);
                BridgeOperationParticipantRecord participant = RequireOwnedParticipant(operation, identity, true);
                if (participant.State == BridgeParticipationState.Attached)
                    participant.State = endState;
                if (!operation.Terminal && operation.ActiveParticipantCount == 0)
                    ApplyAbandonmentLocked(operation);
                StartAvailableLocked();
                TouchLocked(operation);
                PersistLocked();
                return Clone(operation);
            }
        }

        private void ApplyAbandonmentLocked(BridgeOperationRecord operation)
        {
            switch (operation.AbandonmentPolicy)
            {
                case BridgeAbandonmentPolicy.CancelSafely:
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Cancelled,
                        clock.UtcNow, "cleanup", "participant_abandoned", "all participants detached");
                    operation.KeepRunning = false;
                    operation.NextAction = "none";
                    break;
                case BridgeAbandonmentPolicy.LeaveRuntimeRunning:
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Abandoned,
                        clock.UtcNow, "cleanup", "participant_abandoned", "all participants detached");
                    operation.KeepRunning = true;
                    operation.NextAction = "managed runtime remains running; inspect or retire the slot explicitly";
                    break;
                default:
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Succeeded,
                        clock.UtcNow, "cleanup", "participant_abandoned",
                        "safe work completed after all participants detached");
                    operation.KeepRunning = true;
                    operation.NextAction = "complete safe shared work without waiting participants";
                    break;
            }
        }

        private void StartAvailableLocked()
        {
            int active = state.Operations.Count(item => !item.Terminal &&
                (item.OperationState == BridgeOperationState.Running ||
                 item.OperationState == BridgeOperationState.Recovering));
            Dictionary<string, int> activeByAgent = state.Operations.Where(item => !item.Terminal &&
                (item.OperationState == BridgeOperationState.Running ||
                 item.OperationState == BridgeOperationState.Recovering))
                .SelectMany(item => AgentKeys(item)).GroupBy(item => item)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            Dictionary<string, int> activeByClient = state.Operations.Where(item => !item.Terminal &&
                (item.OperationState == BridgeOperationState.Running ||
                 item.OperationState == BridgeOperationState.Recovering))
                .SelectMany(item => ClientKeys(item)).GroupBy(item => item)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            List<BridgeOperationRecord> pending = state.Operations.Where(item => !item.Terminal &&
                (item.OperationState == BridgeOperationState.Queued ||
                 item.OperationState == BridgeOperationState.WaitingForCapacity)).OrderBy(item => item.QueueSequence).ToList();
            while (active < limits.MaximumActiveOperations && pending.Count > 0)
            {
                BridgeOperationRecord operation = pending.FirstOrDefault(item =>
                    !AgentKeys(item).Contains(state.LastAdmittedAgentId));
                if (operation == null) operation = pending[0];
                List<string> agents = AgentKeys(operation).ToList();
                List<string> clients = ClientKeys(operation).ToList();
                if (agents.Any(agent => activeByAgent.ContainsKey(agent) &&
                    activeByAgent[agent] >= limits.MaximumActivePerAgent))
                {
                    operation.CapacityState = "agent_capacity";
                    operation.NextAction = "wait for fair capacity scheduling";
                    pending.Remove(operation);
                    continue;
                }
                if (clients.Any(client => activeByClient.ContainsKey(client) &&
                    activeByClient[client] >= limits.MaximumActivePerClient))
                {
                    operation.CapacityState = "client_capacity";
                    operation.NextAction = "wait for this client instance's active operation quota";
                    pending.Remove(operation);
                    continue;
                }
                BridgeOperationStateMachine.Transition(operation, BridgeOperationState.Running,
                    clock.UtcNow, "runtime");
                operation.CapacityState = "admitted";
                operation.NextAction = "observe progress or wait";
                operation.LaunchIssued = false;
                state.LastAdmittedAgentId = agents.OrderBy(item => item, StringComparer.Ordinal).FirstOrDefault();
                active++;
                foreach (string agent in agents)
                    activeByAgent[agent] = activeByAgent.ContainsKey(agent) ? activeByAgent[agent] + 1 : 1;
                foreach (string client in clients)
                    activeByClient[client] = activeByClient.ContainsKey(client) ? activeByClient[client] + 1 : 1;
                pending.Remove(operation);
            }
            foreach (BridgeOperationRecord operation in pending)
            {
                if (operation.CapacityState != "agent_capacity" && operation.CapacityState != "client_capacity")
                    operation.CapacityState = "global_capacity";
                operation.NextAction = "wait for fair capacity scheduling";
            }
        }

        private int QueuedCountLocked()
        {
            return state.Operations.Count(item => !item.Terminal &&
                (item.OperationState == BridgeOperationState.Queued ||
                 item.OperationState == BridgeOperationState.WaitingForCapacity));
        }

        private int QueuedCountForAgentLocked(string agentId)
        {
            return state.Operations.Count(item => !item.Terminal &&
                (item.OperationState == BridgeOperationState.Queued ||
                 item.OperationState == BridgeOperationState.WaitingForCapacity) &&
                AgentKeys(item).Contains(agentId));
        }

        private int QueuedCountForClientLocked(string agentId, string clientInstanceId)
        {
            return state.Operations.Count(item => !item.Terminal &&
                item.OperationState == BridgeOperationState.Queued &&
                ClientKeys(item).Contains(ClientKey(agentId, clientInstanceId)));
        }

        private int ActiveCountForClientLocked(string agentId, string clientInstanceId)
        {
            return state.Operations.Count(item => !item.Terminal &&
                (item.OperationState == BridgeOperationState.Running ||
                 item.OperationState == BridgeOperationState.Recovering) &&
                ClientKeys(item).Contains(ClientKey(agentId, clientInstanceId)));
        }

        private string ParticipantQuotaFailureLocked(BridgeOperationRecord operation,
            BridgeClientIdentity identity)
        {
            string client = ClientKey(identity.AgentId, identity.ClientInstanceId);
            if (ClientKeys(operation).Contains(client)) return null;
            if ((operation.OperationState == BridgeOperationState.Queued ||
                 operation.OperationState == BridgeOperationState.WaitingForCapacity) &&
                QueuedCountForClientLocked(identity.AgentId, identity.ClientInstanceId) >=
                limits.MaximumQueuedPerClient)
                return "client_queue_limit";
            if ((operation.OperationState == BridgeOperationState.Running ||
                 operation.OperationState == BridgeOperationState.Recovering) &&
                ActiveCountForClientLocked(identity.AgentId, identity.ClientInstanceId) >=
                limits.MaximumActivePerClient)
                return "client_active_limit";
            return null;
        }

        private static IEnumerable<string> AgentKeys(BridgeOperationRecord operation)
        {
            return operation.Participants.Where(item => item.State == BridgeParticipationState.Attached)
                .Select(item => item.AgentId).Where(item => !string.IsNullOrEmpty(item)).Distinct(StringComparer.Ordinal);
        }

        private static IEnumerable<string> ClientKeys(BridgeOperationRecord operation)
        {
            return operation.Participants.Where(item => item.State == BridgeParticipationState.Attached)
                .Where(item => !string.IsNullOrEmpty(item.AgentId) && !string.IsNullOrEmpty(item.ClientInstanceId))
                .Select(item => ClientKey(item.AgentId, item.ClientInstanceId)).Distinct(StringComparer.Ordinal);
        }

        private static string ClientKey(string agentId, string clientInstanceId)
        {
            return (agentId ?? string.Empty) + "\u001f" + (clientInstanceId ?? string.Empty);
        }

        private BridgeOperationRecord NewOperation(BridgeOperationJoinRequest request)
        {
            BridgeOperationKind kind = request.OperationKind;
            BridgeOperationRecord operation = new BridgeOperationRecord
            {
                OperationId = string.IsNullOrWhiteSpace(request.OperationId) ?
                    "operation-" + Guid.NewGuid().ToString("N") : request.OperationId,
                OperationKind = kind,
                OperationState = BridgeOperationState.Pending,
                CompatibilityKey = request.Compatibility.ToString(),
                DesiredState = request.DesiredState,
                RuntimeSlotId = request.RuntimeSlotId ?? string.Empty,
                DeploymentId = request.DeploymentId ?? string.Empty,
                ArtifactFingerprint = request.ArtifactFingerprint ?? string.Empty,
                LoadedAssemblyFingerprint = request.LoadedAssemblyFingerprint ?? string.Empty,
                KeepRunning = request.KeepRunning,
                AbandonmentPolicy = DefaultPolicy(kind),
                CapacityState = "queued",
                Recoverable = true,
                RetrySafe = true,
                NextAction = "wait for fair capacity scheduling",
                RequestedGoalId = request.GoalId ?? string.Empty,
                Version = 2,
                RequestedWorkflow = Bound(request.RequestedWorkflow, 128),
                CurrentPhase = "register",
                DeadlineUtc = request.DeadlineUtc == default(DateTime) ?
                    clock.UtcNow.AddMinutes(5) : request.DeadlineUtc,
                ProgressDeadlineUtc = request.ProgressDeadlineUtc == default(DateTime) ?
                    clock.UtcNow.AddSeconds(30) : request.ProgressDeadlineUtc,
                AuthorizationReference = Bound(request.AuthorizationReference, 256),
                CleanupStatus = "not_started",
                QueueSequence = ++state.QueueSequence,
                CreatedAtUtc = clock.UtcNow,
                UpdatedAtUtc = clock.UtcNow,
                LastProgressAtUtc = clock.UtcNow
            };
            if (!string.IsNullOrWhiteSpace(request.GoalId)) operation.CallerGoalIds.Add(request.GoalId);
            return operation;
        }

        private static BridgeAbandonmentPolicy DefaultPolicy(BridgeOperationKind kind)
        {
            if (kind == BridgeOperationKind.Restart || kind == BridgeOperationKind.SaveLoad ||
                kind == BridgeOperationKind.AdapterReload) return BridgeAbandonmentPolicy.LeaveRuntimeRunning;
            if (kind == BridgeOperationKind.Verification) return BridgeAbandonmentPolicy.CancelSafely;
            return BridgeAbandonmentPolicy.CompleteSafeWork;
        }

        private BridgeOperationJoinResult Result(BridgeOperationRecord operation, string participantId, bool created)
        {
            return new BridgeOperationJoinResult
            {
                Joined = true,
                Created = created,
                OperationId = operation.OperationId,
                ParticipantId = participantId,
                Operation = Clone(operation),
                CapacityState = operation.CapacityState,
                Terminal = operation.Terminal,
                Recoverable = operation.Recoverable,
                RetrySafe = operation.RetrySafe,
                NextAction = operation.NextAction
            };
        }

        private static BridgeOperationJoinResult FailureResult(BridgeOperationRecord operation, string participant,
            string state, bool recoverable)
        {
            return new BridgeOperationJoinResult
            {
                Joined = false,
                OperationId = operation?.OperationId,
                ParticipantId = participant,
                Operation = operation == null ? null : Clone(operation),
                CapacityState = state,
                Recoverable = recoverable,
                RetrySafe = recoverable,
                NextAction = recoverable ? "retry after the participant or queue limit changes" : "none"
            };
        }

        private static BridgeOperationJoinResult CapacityResult(BridgeOperationRecord operation, string participant,
            string capacity, string nextAction)
        {
            return new BridgeOperationJoinResult
            {
                Joined = false,
                OperationId = operation?.OperationId,
                ParticipantId = participant,
                Operation = operation == null ? null : Clone(operation),
                CapacityState = capacity,
                Recoverable = true,
                RetrySafe = true,
                NextAction = nextAction
            };
        }

        private BridgeOperationRecord Find(string operationId)
        {
            BridgeOperationRecord operation;
            if (!byId.TryGetValue(operationId ?? string.Empty, out operation))
                throw new InvalidOperationException("operation_not_found");
            return operation;
        }

        private static BridgeOperationParticipantRecord RequireParticipant(BridgeOperationRecord operation,
            string participantId, bool allowDetached)
        {
            if (operation == null) throw new InvalidOperationException("operation_not_found");
            BridgeOperationParticipantRecord participant = operation.Participants.FirstOrDefault(item =>
                string.Equals(item.ParticipantId, participantId, StringComparison.Ordinal));
            if (participant == null || (!allowDetached && participant.State != BridgeParticipationState.Attached))
                throw new InvalidOperationException("operation_participant_not_attached");
            return participant;
        }

        private static BridgeOperationParticipantRecord RequireOwnedParticipant(
            BridgeOperationRecord operation, BridgeClientIdentity identity, bool allowDetached)
        {
            ValidateIdentity(identity);
            BridgeOperationParticipantRecord participant = RequireParticipant(operation,
                identity.ParticipantId, allowDetached);
            if (!string.Equals(participant.AgentId, identity.AgentId, StringComparison.Ordinal) ||
                !string.Equals(participant.ClientInstanceId, identity.ClientInstanceId,
                    StringComparison.Ordinal)) throw new InvalidOperationException("participant_identity_mismatch");
            return participant;
        }

        private BridgeOperationParticipantRecord NewParticipant(BridgeClientIdentity identity)
        {
            return new BridgeOperationParticipantRecord
            {
                ParticipantId = identity.ParticipantId,
                AgentId = identity.AgentId,
                ClientInstanceId = identity.ClientInstanceId,
                ConnectionSessionId = identity.ConnectionSessionId,
                RequestCorrelationId = identity.RequestCorrelationId,
                State = BridgeParticipationState.Attached,
                JoinedAtUtc = clock.UtcNow,
                LastSeenAtUtc = clock.UtcNow,
                SanitizedAgentId = identity.SanitizedAgentId,
                SanitizedClientInstanceId = identity.SanitizedClientInstanceId
            };
        }

        private static string PrimaryAgent(BridgeOperationRecord operation)
        {
            return operation.Participants.FirstOrDefault()?.AgentId ?? "anonymous";
        }

        private static string PrimaryClient(BridgeOperationRecord operation)
        {
            return operation.Participants.FirstOrDefault()?.ClientInstanceId ?? "client-legacy";
        }

        private void TouchLocked(BridgeOperationRecord operation)
        {
            operation.UpdatedAtUtc = clock.UtcNow;
            state.Sequence++;
        }

        private int AdvanceDeadlinesLocked()
        {
            DateTime now = clock.UtcNow;
            int changed = 0;
            foreach (BridgeOperationRecord operation in state.Operations.Where(item => !item.Terminal).ToList())
            {
                if (operation.DeadlineUtc != default(DateTime) && now >= operation.DeadlineUtc)
                {
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.TimedOut, now,
                        "cleanup", "operation_deadline_expired", "operation deadline elapsed");
                    operation.FailureCode = "operation_deadline_expired";
                    operation.Recoverable = true;
                    operation.RetrySafe = true;
                    operation.NextAction = "retry the operation after inspecting cleanup evidence";
                    TouchLocked(operation);
                    changed++;
                    continue;
                }
                if ((operation.OperationState == BridgeOperationState.Running ||
                    operation.OperationState == BridgeOperationState.Recovering) &&
                    operation.ProgressDeadlineUtc != default(DateTime) &&
                    now >= operation.ProgressDeadlineUtc)
                {
                    BridgeOperationStateMachine.Transition(operation, BridgeOperationState.TimedOut, now,
                        "cleanup", "progress_deadline_expired", "no progress was observed before the deadline");
                    operation.FailureCode = "progress_deadline_expired";
                    operation.Recoverable = true;
                    operation.RetrySafe = true;
                    operation.NextAction = "reconnect or retry after inspecting cleanup evidence";
                    TouchLocked(operation);
                    changed++;
                }
            }
            if (changed > 0) StartAvailableLocked();
            return changed;
        }

        private void PersistLocked()
        {
            if (!string.IsNullOrWhiteSpace(statePath)) BridgeDurableJson.WriteAtomic(statePath, state);
        }

        private void ReloadLocked()
        {
            if (string.IsNullOrWhiteSpace(statePath)) return;
            BridgeSharedOperationState persisted = BridgeDurableJson.Read<BridgeSharedOperationState>(statePath);
            if (persisted == null) return;
            if (persisted.Operations == null) persisted.Operations = new List<BridgeOperationRecord>();
            state = persisted;
            byId.Clear();
            foreach (BridgeOperationRecord operation in state.Operations)
            {
                Normalize(operation);
                byId[operation.OperationId] = operation;
            }
        }

        private static void ValidateIdentity(BridgeClientIdentity identity)
        {
            if (!BridgeIdentityRules.IsValid(identity.AgentId) ||
                !BridgeIdentityRules.IsValid(identity.ClientInstanceId) ||
                !BridgeIdentityRules.IsValid(identity.ConnectionSessionId) ||
                !BridgeIdentityRules.IsValid(identity.RequestCorrelationId) ||
                !BridgeIdentityRules.IsValid(identity.ParticipantId))
                throw new ArgumentException("operation_identity_invalid");
        }

        private static bool SameProcess(BridgeOperationRecord operation, BridgeProcessIdentity process)
        {
            return operation.Pid == process.Pid &&
                string.Equals(operation.ProcessStartIdentity, process.ProcessStartIdentity,
                    StringComparison.Ordinal) &&
                string.Equals(operation.SessionId, process.SessionId, StringComparison.Ordinal) &&
                operation.LifecycleGeneration == process.LifecycleGeneration &&
                string.Equals(operation.RuntimeSlotId, process.RuntimeSlotId, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(operation.LoadedAssemblyFingerprint) ||
                 string.Equals(operation.LoadedAssemblyFingerprint, process.LoadedAssemblyFingerprint,
                     StringComparison.OrdinalIgnoreCase));
        }

        private static string Bound(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        private static void Normalize(BridgeOperationRecord operation)
        {
            if (operation.Participants == null) operation.Participants = new List<BridgeOperationParticipantRecord>();
            if (operation.CallerGoalIds == null) operation.CallerGoalIds = new List<string>();
            if (string.IsNullOrEmpty(operation.OperationId)) operation.OperationId = "operation-" + Guid.NewGuid().ToString("N");
            if (operation.LastProgressAtUtc == default(DateTime)) operation.LastProgressAtUtc = operation.UpdatedAtUtc;
            if (operation.UpdatedAtUtc == default(DateTime)) operation.UpdatedAtUtc = operation.CreatedAtUtc;
            if (operation.Version <= 0) operation.Version = 1;
            if (operation.CompletedPhases == null) operation.CompletedPhases = new List<string>();
            if (string.IsNullOrEmpty(operation.CurrentPhase)) operation.CurrentPhase =
                operation.OperationState.ToString().ToLowerInvariant();
            if (string.IsNullOrEmpty(operation.CleanupStatus)) operation.CleanupStatus =
                operation.Terminal ? "complete" : "not_started";
            if (operation.DeadlineUtc == default(DateTime) && operation.CreatedAtUtc != default(DateTime))
                operation.DeadlineUtc = operation.CreatedAtUtc.AddMinutes(5);
            if (operation.ProgressDeadlineUtc == default(DateTime) &&
                (operation.OperationState == BridgeOperationState.Running ||
                 operation.OperationState == BridgeOperationState.Recovering))
                operation.ProgressDeadlineUtc = operation.LastProgressAtUtc.AddSeconds(30);
        }

        private static BridgeOperationRecord Clone(BridgeOperationRecord source)
        {
            BridgeOperationRecord result = new BridgeOperationRecord
            {
                OperationId = source.OperationId,
                OperationKind = source.OperationKind,
                OperationState = source.OperationState,
                CompatibilityKey = source.CompatibilityKey,
                DesiredState = source.DesiredState,
                RuntimeSlotId = source.RuntimeSlotId,
                DeploymentId = source.DeploymentId,
                ArtifactFingerprint = source.ArtifactFingerprint,
                LoadedAssemblyFingerprint = source.LoadedAssemblyFingerprint,
                Pid = source.Pid,
                ProcessStartIdentity = source.ProcessStartIdentity,
                SessionId = source.SessionId,
                LifecycleGeneration = source.LifecycleGeneration,
                ProgressSequence = source.ProgressSequence,
                LastProgressAtUtc = source.LastProgressAtUtc,
                Terminal = source.Terminal,
                Recoverable = source.Recoverable,
                RetrySafe = source.RetrySafe,
                NextAction = source.NextAction,
                CapacityState = source.CapacityState,
                KeepRunning = source.KeepRunning,
                AbandonmentPolicy = source.AbandonmentPolicy,
                LaunchIssued = source.LaunchIssued,
                CoordinatorRestarted = source.CoordinatorRestarted,
                FailureCode = source.FailureCode,
                RequestedGoalId = source.RequestedGoalId,
                QueueSequence = source.QueueSequence,
                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,
                Version = source.Version,
                RequestedWorkflow = source.RequestedWorkflow,
                CurrentPhase = source.CurrentPhase,
                CompletedPhases = source.CompletedPhases.ToList(),
                DeadlineUtc = source.DeadlineUtc,
                ProgressDeadlineUtc = source.ProgressDeadlineUtc,
                AuthorizationReference = source.AuthorizationReference,
                TerminalResultCode = source.TerminalResultCode,
                TerminalResultDetail = source.TerminalResultDetail,
                CleanupStatus = source.CleanupStatus,
                CallerGoalIds = source.CallerGoalIds.ToList(),
                Participants = source.Participants.Select(item => new BridgeOperationParticipantRecord
                {
                    ParticipantId = item.ParticipantId,
                    AgentId = item.AgentId,
                    ClientInstanceId = item.ClientInstanceId,
                    ConnectionSessionId = item.ConnectionSessionId,
                    RequestCorrelationId = item.RequestCorrelationId,
                    State = item.State,
                    JoinedAtUtc = item.JoinedAtUtc,
                    LastSeenAtUtc = item.LastSeenAtUtc,
                    LastObservedProgressSequence = item.LastObservedProgressSequence,
                    SanitizedAgentId = item.SanitizedAgentId,
                    SanitizedClientInstanceId = item.SanitizedClientInstanceId
                }).ToList()
            };
            return result;
        }
    }
}
