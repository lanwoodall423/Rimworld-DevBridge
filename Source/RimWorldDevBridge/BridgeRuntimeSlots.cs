using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace RimWorldDevBridge
{
    [DataContract]
    public sealed class BridgeRuntimeSlotDefinition
    {
        [DataMember(Order = 1)] public string RuntimeSlotId;
        [DataMember(Order = 2)] public string ManagedProfile;
        [DataMember(Order = 3)] public string UserDataRoot;
        [DataMember(Order = 4)] public string ModConfigurationFingerprint;
        [DataMember(Order = 5)] public string DeploymentOverlayRoot;
        [DataMember(Order = 6)] public string CoordinatorRoot;
        [DataMember(Order = 7)] public string ProcessRoot;
        [DataMember(Order = 8)] public string IpcRoot;
        [DataMember(Order = 9)] public string SaveRoot;
        [DataMember(Order = 10)] public string LogRoot;
        [DataMember(Order = 11)] public string EvidenceRoot;
        [DataMember(Order = 12)] public string ResourceRoot;
        [DataMember(Order = 13)] public string ConfigFingerprint;
        [DataMember(Order = 14)] public string ModLoadOrderFingerprint;
        [DataMember(Order = 15)] public string LifecycleGeneration;
        [DataMember(Order = 16)] public bool ManagedOwnership;
        [DataMember(Order = 17)] public bool ActiveProcess;
        [DataMember(Order = 18)] public BridgeProcessIdentity Process;
        [DataMember(Order = 19)] public string CompatibilityFingerprint;
        [DataMember(Order = 20)] public long LastQueueSequence;
        [DataMember(Order = 21)] public BridgeProcessIdentity ExpectedProcess;
        [DataMember(Order = 22)] public List<BridgeRuntimeSlotLease> ActiveLeases =
            new List<BridgeRuntimeSlotLease>();
        [DataMember(Order = 23)] public string ExpectedOperationId;
        [DataMember(Order = 24)] public string ExpectedAgentId;
        [DataMember(Order = 25)] public string ExpectedClientInstanceId;
    }

    [DataContract]
    public sealed class BridgeRuntimeSlotLease
    {
        [DataMember(Order = 1)] public string OperationId;
        [DataMember(Order = 2)] public string AgentId;
        [DataMember(Order = 3)] public string ClientInstanceId;
    }

    [DataContract]
    public sealed class BridgeRuntimeSlotRequest
    {
        [DataMember(Order = 1)]
        public BridgeOperationCompatibilityKey Compatibility;
        [DataMember(Order = 2)]
        public string RequestedRuntimeSlotId;
        [DataMember(Order = 3)]
        public string AgentId;
        [DataMember(Order = 4)]
        public string ClientInstanceId;
        [DataMember(Order = 5)]
        public string OperationId;
        [DataMember(Order = 6)]
        public bool RequiresNewProcess;
        [DataMember(Order = 7)]
        public bool RejectIfUnavailable;
    }

    [DataContract]
    internal sealed class BridgeRuntimeSlotState
    {
        [DataMember(Order = 1)] public int SchemaVersion = 1;
        [DataMember(Order = 2)] public long QueueSequence;
        [DataMember(Order = 3)] public List<BridgeRuntimeSlotDefinition> Slots =
            new List<BridgeRuntimeSlotDefinition>();
        [DataMember(Order = 4)] public List<BridgeRuntimeSlotRequest> QueuedRequests =
            new List<BridgeRuntimeSlotRequest>();
    }

    public sealed class BridgeRuntimeSlotAllocation
    {
        public bool Allocated;
        public bool Reused;
        public string CapacityState;
        public string NextAction;
        public BridgeRuntimeSlotDefinition Slot;
        public long QueueSequence;
    }

    public sealed class BridgeRuntimeSlotManager
    {
        private readonly object gate = new object();
        private readonly IBridgeClock clock;
        private readonly int maximumActiveProcesses;
        private readonly string root;
        private readonly string statePath;
        private readonly Dictionary<string, BridgeRuntimeSlotDefinition> slots =
            new Dictionary<string, BridgeRuntimeSlotDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<BridgeRuntimeSlotRequest>> queues =
            new Dictionary<string, Queue<BridgeRuntimeSlotRequest>>(StringComparer.Ordinal);
        private readonly List<string> queueAgents = new List<string>();
        private long queueSequence;
        private int roundRobinIndex;
        private int durableStateDepth;

        public BridgeRuntimeSlotManager(int maximumActiveProcesses, IBridgeClock clock = null, string root = null)
        {
            this.maximumActiveProcesses = Math.Max(1, maximumActiveProcesses);
            this.clock = clock ?? new BridgeSystemClock();
            this.root = Path.GetFullPath(root ?? Path.Combine(Path.GetTempPath(), "RimWorldDevBridge",
                "slots-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(this.root);
            statePath = Path.Combine(this.root, "runtime-slots.json");
            BridgeRuntimeSlotState persisted = BridgeDurableJson.Read<BridgeRuntimeSlotState>(statePath);
            if (persisted != null)
            {
                queueSequence = persisted.QueueSequence;
                foreach (BridgeRuntimeSlotDefinition slot in persisted.Slots ??
                    new List<BridgeRuntimeSlotDefinition>())
                {
                    if (slot.ActiveLeases == null) slot.ActiveLeases = new List<BridgeRuntimeSlotLease>();
                    if (slot.ActiveProcess && slot.Process == null && slot.ExpectedProcess == null)
                    {
                        // A durable slot without a verifiable process identity cannot be claimed after recovery.
                        // Keep it reserved until an operator or an owning coordinator resolves it.
                        slot.ManagedOwnership = false;
                    }
                    else if (slot.ActiveProcess && slot.Process != null && !IsLiveManagedProcess(slot))
                    {
                        // A persisted PID is not ownership. Preserve a live-but-unverifiable
                        // process as operator-owned and clear exited ownership immediately.
                        slot.ManagedOwnership = false;
                    }
                    else if (slot.ActiveProcess && slot.Process == null && slot.ExpectedProcess != null &&
                        !IsLiveExpectedProcess(slot))
                    {
                        slot.ManagedOwnership = false;
                    }
                    slots[slot.RuntimeSlotId] = slot;
                }
                foreach (BridgeRuntimeSlotRequest request in persisted.QueuedRequests ??
                    new List<BridgeRuntimeSlotRequest>()) EnqueueLoaded(request);
            }
        }

        public int MaximumActiveProcesses => maximumActiveProcesses;

        public BridgeRuntimeSlotAllocation Allocate(BridgeRuntimeSlotRequest request)
        {
            if (request == null || request.Compatibility == null) throw new ArgumentException("slot_request_invalid");
            if (!BridgeIdentityRules.IsValid(request.AgentId) ||
                !BridgeIdentityRules.IsValid(request.ClientInstanceId))
                throw new ArgumentException("slot_identity_invalid");
            if (string.IsNullOrWhiteSpace(request.OperationId))
                request.OperationId = "operation-" + Guid.NewGuid().ToString("N");
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition requested;
                if (!string.IsNullOrEmpty(request.RequestedRuntimeSlotId))
                {
                    if (!slots.TryGetValue(request.RequestedRuntimeSlotId, out requested))
                    {
                        if (ActiveProcessCountLocked() >= maximumActiveProcesses)
                            return QueueLocked(request, "global_process_capacity");
                        BridgeRuntimeSlotDefinition createdRequested = CreateSlot(request,
                            request.RequestedRuntimeSlotId);
                        slots.Add(createdRequested.RuntimeSlotId, createdRequested);
                        AddLeaseLocked(createdRequested, request);
                        PersistLocked();
                        return Allocation(createdRequested, false, "admitted");
                    }
                    if (!requested.ManagedOwnership)
                        return QueueLocked(request, "attached_live_process_requires_operator");
                    if (requested.Process != null && requested.Process.Pid !=
                        System.Diagnostics.Process.GetCurrentProcess().Id)
                        return QueueLocked(request, "attached_live_process_requires_operator");
                    if (!Compatible(requested, request.Compatibility) ||
                        request.RequiresNewProcess)
                        return QueueLocked(request, "incompatible_slot_requires_isolation");
                    if (requested.ActiveProcess || ActiveProcessCountLocked() < maximumActiveProcesses)
                    {
                        requested.ActiveProcess = true;
                        AddLeaseLocked(requested, request);
                        PersistLocked();
                        return Allocation(requested, true, "admitted");
                    }
                    return QueueLocked(request, "global_process_capacity");
                }

                BridgeRuntimeSlotDefinition reusable = slots.Values.Where(item =>
                    item.ManagedOwnership && Compatible(item, request.Compatibility) && !request.RequiresNewProcess &&
                    (item.Process == null || item.Process.Pid == System.Diagnostics.Process.GetCurrentProcess().Id))
                    .OrderBy(item => item.RuntimeSlotId, StringComparer.Ordinal).FirstOrDefault();
                if (reusable != null)
                {
                    reusable.ActiveProcess = true;
                    AddLeaseLocked(reusable, request);
                    PersistLocked();
                    return Allocation(reusable, true, "admitted");
                }
                if (ActiveProcessCountLocked() >= maximumActiveProcesses)
                    return QueueLocked(request, "global_process_capacity");
                BridgeRuntimeSlotDefinition created = CreateSlot(request, null);
                slots.Add(created.RuntimeSlotId, created);
                AddLeaseLocked(created, request);
                PersistLocked();
                return Allocation(created, false, "admitted");
            }
        }

        public BridgeRuntimeSlotAllocation TryAllocateNext()
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                if (queueAgents.Count == 0)
                    return new BridgeRuntimeSlotAllocation { CapacityState = "queue_empty", NextAction = "none" };
                int attempts = queueAgents.Count;
                BridgeRuntimeSlotAllocation last = null;
                for (int attempt = 0; attempt < attempts && queueAgents.Count > 0; attempt++)
                {
                    if (roundRobinIndex >= queueAgents.Count) roundRobinIndex = 0;
                    string agent = queueAgents[roundRobinIndex++];
                    Queue<BridgeRuntimeSlotRequest> queue = queues[agent];
                    if (queue.Count == 0) continue;
                    BridgeRuntimeSlotRequest request = queue.Dequeue();
                    if (queue.Count == 0)
                    {
                        queues.Remove(agent);
                        queueAgents.Remove(agent);
                        roundRobinIndex = Math.Max(0, roundRobinIndex - 1);
                    }
                    BridgeRuntimeSlotAllocation result = Allocate(request);
                    if (result.Allocated) return result;
                    last = result;
                }
                return last ?? new BridgeRuntimeSlotAllocation { CapacityState = "queue_empty", NextAction = "none" };
            }
        }

        public BridgeRuntimeSlotDefinition FindLease(string operationId, string agentId, string clientInstanceId)
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot = slots.Values.FirstOrDefault(item =>
                    item.ActiveLeases.Any(lease =>
                        string.Equals(lease.OperationId, operationId, StringComparison.Ordinal) &&
                        string.Equals(lease.AgentId, agentId, StringComparison.Ordinal) &&
                        string.Equals(lease.ClientInstanceId, clientInstanceId, StringComparison.Ordinal)));
                return slot == null ? null : Clone(slot);
            }
        }

        public bool ClaimManagedProcess(string runtimeSlotId, BridgeProcessIdentity process)
        {
            return false;
        }

        public bool ClaimManagedProcess(string runtimeSlotId, BridgeProcessIdentity process,
            string operationId, string agentId, string clientInstanceId)
        {
            if (process == null) return false;
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot;
                if (!slots.TryGetValue(runtimeSlotId ?? string.Empty, out slot) || !slot.ManagedOwnership)
                    return false;
                if (!string.Equals(process.RuntimeSlotId, runtimeSlotId, StringComparison.Ordinal)) return false;
                if (slot.Process != null)
                {
                    if (!SameProcess(slot.Process, process) || !HasLease(slot, operationId, agentId, clientInstanceId))
                        return false;
                    PersistLocked();
                    return true;
                }
                if (!OwnerMatches(slot.ExpectedOperationId, slot.ExpectedAgentId, slot.ExpectedClientInstanceId,
                    operationId, agentId, clientInstanceId)) return false;
                if (slot.ExpectedProcess == null || !SameProcess(slot.ExpectedProcess, process)) return false;
                slot.Process = CloneProcess(process);
                slot.ExpectedProcess = null;
                slot.ExpectedOperationId = null;
                slot.ExpectedAgentId = null;
                slot.ExpectedClientInstanceId = null;
                slot.ActiveProcess = true;
                PersistLocked();
                return true;
            }
        }

        public bool RecordManagedLaunch(string runtimeSlotId, BridgeProcessIdentity expected)
        {
            return false;
        }

        internal bool RecordManagedLaunch(string runtimeSlotId, BridgeProcessIdentity expected,
            string operationId, string agentId, string clientInstanceId)
        {
            if (expected == null) return false;
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot;
                if (!slots.TryGetValue(runtimeSlotId ?? string.Empty, out slot) || !slot.ManagedOwnership ||
                    !slot.ActiveProcess || !string.Equals(expected.RuntimeSlotId, runtimeSlotId,
                        StringComparison.Ordinal)) return false;
                slot.ExpectedProcess = CloneProcess(expected);
                slot.ExpectedOperationId = operationId ?? string.Empty;
                slot.ExpectedAgentId = agentId ?? string.Empty;
                slot.ExpectedClientInstanceId = clientInstanceId ?? string.Empty;
                PersistLocked();
                return true;
            }
        }

        public bool RecordCoordinatorLaunch(BridgeRuntimeSlotRequest request, BridgeProcessIdentity expected)
        {
            if (request == null || expected == null || string.IsNullOrWhiteSpace(request.RequestedRuntimeSlotId) ||
                expected.Pid <= 0 || string.IsNullOrWhiteSpace(expected.ProcessStartIdentity) ||
                string.IsNullOrWhiteSpace(expected.ExecutablePath) ||
                !string.Equals(expected.RuntimeSlotId, request.RequestedRuntimeSlotId, StringComparison.Ordinal))
                return false;
            request.RejectIfUnavailable = true;
            RemoveEmptyCoordinatorSlot(request.RequestedRuntimeSlotId);
            BridgeRuntimeSlotAllocation allocation = Allocate(request);
            if (!allocation.Allocated || allocation.Slot == null)
                return false;
            if (allocation.Slot.Process != null)
            {
                Release(allocation.Slot.RuntimeSlotId, request.OperationId, request.AgentId,
                    request.ClientInstanceId, false);
                return false;
            }
            bool recorded = RecordManagedLaunch(allocation.Slot.RuntimeSlotId, expected, request.OperationId,
                request.AgentId, request.ClientInstanceId);
            if (!recorded)
                Release(allocation.Slot.RuntimeSlotId, request.OperationId, request.AgentId,
                    request.ClientInstanceId, false);
            return recorded;
        }

        private void RemoveEmptyCoordinatorSlot(string runtimeSlotId)
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot;
                if (!slots.TryGetValue(runtimeSlotId ?? string.Empty, out slot) ||
                    slot.Process != null || slot.ExpectedProcess != null ||
                    (slot.ActiveLeases != null && slot.ActiveLeases.Count != 0)) return;
                slots.Remove(runtimeSlotId);
                PersistLocked();
            }
        }

        public bool ClearCoordinatorLaunch(string runtimeSlotId, string operationId, string agentId,
            string clientInstanceId, BridgeProcessIdentity expected)
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot;
                if (!slots.TryGetValue(runtimeSlotId ?? string.Empty, out slot) ||
                    !OwnerMatches(slot.ExpectedOperationId, slot.ExpectedAgentId, slot.ExpectedClientInstanceId,
                        operationId, agentId, clientInstanceId)) return false;
                if (slot.ExpectedProcess != null && expected != null && !SameProcess(slot.ExpectedProcess, expected))
                    return false;
                slot.ExpectedProcess = null;
                slot.ExpectedOperationId = null;
                slot.ExpectedAgentId = null;
                slot.ExpectedClientInstanceId = null;
                slot.ActiveLeases.RemoveAll(item => string.Equals(item.OperationId, operationId,
                    StringComparison.Ordinal) && string.Equals(item.AgentId, agentId,
                    StringComparison.Ordinal) && string.Equals(item.ClientInstanceId, clientInstanceId,
                    StringComparison.Ordinal));
                if (slot.Process != null && expected != null && SameProcess(slot.Process, expected))
                    slot.Process = null;
                slot.ActiveProcess = slot.Process != null || slot.ActiveLeases.Count > 0;
                if (!slot.ActiveProcess) slots.Remove(runtimeSlotId);
                PersistLocked();
                return true;
            }
        }

        public string RefuseAttachedProcess(string runtimeSlotId, BridgeProcessIdentity observed)
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot;
                if (!slots.TryGetValue(runtimeSlotId ?? string.Empty, out slot)) return "slot_not_found";
                if (slot.ExpectedProcess != null && observed != null && SameProcess(slot.ExpectedProcess, observed) &&
                    slot.Process == null) return "managed_launch_pending";
                if (slot.Process == null && observed != null) return "attached_live_process_requires_operator";
                if (slot.Process != null && observed != null && !SameProcess(slot.Process, observed))
                    return "stale_process_identity";
                return slot.ManagedOwnership ? "managed_process" : "attached_live_process_requires_operator";
            }
        }

        public bool Release(string runtimeSlotId, string operationId, string agentId, string clientInstanceId,
            bool keepRunning)
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot;
                if (!slots.TryGetValue(runtimeSlotId ?? string.Empty, out slot)) return false;
                BridgeRuntimeSlotLease lease = slot.ActiveLeases.FirstOrDefault(item =>
                    string.Equals(item.OperationId, operationId, StringComparison.Ordinal) &&
                    string.Equals(item.AgentId, agentId, StringComparison.Ordinal) &&
                    string.Equals(item.ClientInstanceId, clientInstanceId, StringComparison.Ordinal));
                if (lease == null) return false;
                slot.ActiveLeases.Remove(lease);
                slot.ActiveProcess = keepRunning || slot.ActiveLeases.Count > 0;
                if (!slot.ActiveProcess)
                {
                    slot.Process = null;
                    slot.ExpectedProcess = null;
                }
                PersistLocked();
                return true;
            }
        }

        public bool Retire(string runtimeSlotId)
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                BridgeRuntimeSlotDefinition slot;
                if (!slots.TryGetValue(runtimeSlotId ?? string.Empty, out slot) || slot.ActiveProcess) return false;
                bool removed = slots.Remove(runtimeSlotId);
                if (removed) PersistLocked();
                return removed;
            }
        }

        public List<BridgeRuntimeSlotDefinition> Snapshot()
        {
            lock (gate)
            using (EnterDurableStateLock())
            {
                if (durableStateDepth == 1) ReloadFromDiskLocked();
                return slots.Values.Select(Clone).OrderBy(item => item.RuntimeSlotId,
                    StringComparer.Ordinal).ToList();
            }
        }

        public bool ValidateIsolation(BridgeRuntimeSlotDefinition first, BridgeRuntimeSlotDefinition second)
        {
            if (first == null || second == null || string.Equals(first.RuntimeSlotId, second.RuntimeSlotId,
                StringComparison.Ordinal)) return false;
            string[] paths = new[] { first.UserDataRoot, first.CoordinatorRoot, first.ProcessRoot, first.IpcRoot,
                first.SaveRoot, first.LogRoot, first.EvidenceRoot, first.ResourceRoot, first.DeploymentOverlayRoot };
            string[] other = new[] { second.UserDataRoot, second.CoordinatorRoot, second.ProcessRoot, second.IpcRoot,
                second.SaveRoot, second.LogRoot, second.EvidenceRoot, second.ResourceRoot,
                second.DeploymentOverlayRoot };
            return paths.All(path => string.IsNullOrEmpty(path) || other.All(value => !PathsOverlap(path, value)));
        }

        public int ActiveProcessCount
        {
            get
            {
                lock (gate)
                using (EnterDurableStateLock())
                {
                    if (durableStateDepth == 1) ReloadFromDiskLocked();
                    return ActiveProcessCountLocked();
                }
            }
        }

        public int QueuedRequestCount
        {
            get
            {
                lock (gate)
                using (EnterDurableStateLock())
                {
                    if (durableStateDepth == 1) ReloadFromDiskLocked();
                    return queues.Values.Sum(queue => queue.Count);
                }
            }
        }

        private BridgeRuntimeSlotAllocation QueueLocked(BridgeRuntimeSlotRequest request, string state)
        {
            if (request.RejectIfUnavailable)
                return new BridgeRuntimeSlotAllocation { Allocated = false, CapacityState = state,
                    NextAction = "managed launch could not claim an immediately available slot" };
            string agent = QueueKey(request);
            Queue<BridgeRuntimeSlotRequest> queue;
            if (!queues.TryGetValue(agent, out queue))
            {
                queue = new Queue<BridgeRuntimeSlotRequest>();
                queues.Add(agent, queue);
                queueAgents.Add(agent);
            }
            request.OperationId = request.OperationId ?? "operation-" + Guid.NewGuid().ToString("N");
            queue.Enqueue(request);
            queueSequence++;
            PersistLocked();
            return new BridgeRuntimeSlotAllocation { Allocated = false, CapacityState = state,
                QueueSequence = queueSequence, NextAction = "wait for fair structured slot capacity" };
        }

        private BridgeRuntimeSlotAllocation Allocation(BridgeRuntimeSlotDefinition slot, bool reused, string state)
        {
            queueSequence++;
            PersistLocked();
            return new BridgeRuntimeSlotAllocation { Allocated = true, Reused = reused, Slot = Clone(slot),
                CapacityState = state, QueueSequence = queueSequence, NextAction = "start or join work in this slot" };
        }

        private BridgeRuntimeSlotDefinition CreateSlot(BridgeRuntimeSlotRequest request, string requestedId)
        {
            string digest = CompatibilityDigest(request.Compatibility).Substring(0, 16).ToLowerInvariant();
            string id = string.IsNullOrWhiteSpace(requestedId) ? "slot-" + digest : requestedId;
            if (!IsSafeSlotId(id)) throw new ArgumentException("runtime_slot_id_invalid");
            int suffix = 0;
            while (slots.ContainsKey(id)) id = "slot-" + digest + "-" + (++suffix).ToString();
            string baseRoot = Path.Combine(root, id);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string fullBase = Path.GetFullPath(baseRoot);
            if (!fullBase.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("runtime_slot_path_escape");
            BridgeRuntimeSlotDefinition slot = new BridgeRuntimeSlotDefinition
            {
                RuntimeSlotId = id,
                ManagedProfile = request.Compatibility.ManagedProfile,
                UserDataRoot = Path.Combine(baseRoot, "user"),
                ModConfigurationFingerprint = request.Compatibility.ModSetFingerprint,
                DeploymentOverlayRoot = Path.Combine(baseRoot, "overlay"),
                CoordinatorRoot = Path.Combine(baseRoot, "coordinator"),
                ProcessRoot = Path.Combine(baseRoot, "process"),
                IpcRoot = Path.Combine(baseRoot, "ipc"),
                SaveRoot = Path.Combine(baseRoot, "saves"),
                LogRoot = Path.Combine(baseRoot, "logs"),
                EvidenceRoot = Path.Combine(baseRoot, "evidence"),
                ResourceRoot = Path.Combine(baseRoot, "resources"),
                ConfigFingerprint = request.Compatibility.ConfigurationFingerprint,
                ModLoadOrderFingerprint = request.Compatibility.ModLoadOrderFingerprint,
                LifecycleGeneration = request.Compatibility.LifecycleGeneration.ToString(),
                ManagedOwnership = true,
                ActiveProcess = true,
                CompatibilityFingerprint = CompatibilityDigest(request.Compatibility),
                LastQueueSequence = queueSequence
            };
            return slot;
        }

        private static bool IsSafeSlotId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.Length > 128)
                return false;
            return value.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_');
        }

        private void AddLeaseLocked(BridgeRuntimeSlotDefinition slot, BridgeRuntimeSlotRequest request)
        {
            if (slot.ActiveLeases == null) slot.ActiveLeases = new List<BridgeRuntimeSlotLease>();
            if (!slot.ActiveLeases.Any(item => string.Equals(item.OperationId, request.OperationId,
                    StringComparison.Ordinal) && string.Equals(item.AgentId, request.AgentId,
                    StringComparison.Ordinal) && string.Equals(item.ClientInstanceId,
                    request.ClientInstanceId, StringComparison.Ordinal)))
                slot.ActiveLeases.Add(new BridgeRuntimeSlotLease
                {
                    OperationId = request.OperationId,
                    AgentId = request.AgentId,
                    ClientInstanceId = request.ClientInstanceId
                });
        }

        private void EnqueueLoaded(BridgeRuntimeSlotRequest request)
        {
            if (request == null || request.Compatibility == null) return;
            string key = QueueKey(request);
            Queue<BridgeRuntimeSlotRequest> queue;
            if (!queues.TryGetValue(key, out queue))
            {
                queue = new Queue<BridgeRuntimeSlotRequest>();
                queues.Add(key, queue);
                queueAgents.Add(key);
            }
            queue.Enqueue(request);
        }

        private void PersistLocked()
        {
            BridgeRuntimeSlotState persisted = new BridgeRuntimeSlotState
            {
                QueueSequence = queueSequence,
                Slots = slots.Values.Select(Clone).ToList(),
                QueuedRequests = queues.Values.SelectMany(queue => queue).ToList()
            };
            BridgeDurableJson.WriteAtomic(statePath, persisted);
        }

        private IDisposable EnterDurableStateLock()
        {
            if (durableStateDepth > 0)
            {
                durableStateDepth++;
                return new SlotStateLease(this, null);
            }
            IDisposable stateLock = BridgeDurableJson.AcquireStateLock(statePath);
            durableStateDepth = 1;
            return new SlotStateLease(this, stateLock);
        }

        private void ReloadFromDiskLocked()
        {
            BridgeRuntimeSlotState persisted = BridgeDurableJson.Read<BridgeRuntimeSlotState>(statePath);
            if (persisted == null) return;
            HashSet<string> locallyPending = new HashSet<string>(slots.Values
                .Where(item => item.ManagedOwnership && item.ActiveProcess && item.Process == null)
                .Select(item => item.RuntimeSlotId), StringComparer.Ordinal);
            slots.Clear();
            queues.Clear();
            queueAgents.Clear();
            queueSequence = persisted.QueueSequence;
            roundRobinIndex = 0;
            foreach (BridgeRuntimeSlotDefinition slot in persisted.Slots ??
                new List<BridgeRuntimeSlotDefinition>())
            {
                if (slot.ActiveLeases == null) slot.ActiveLeases = new List<BridgeRuntimeSlotLease>();
                if (slot.ActiveProcess && ((slot.Process == null && slot.ExpectedProcess == null &&
                    !locallyPending.Contains(slot.RuntimeSlotId)) ||
                    (slot.Process != null && !IsLiveManagedProcess(slot)) ||
                    (slot.Process == null && slot.ExpectedProcess != null && !IsLiveExpectedProcess(slot))))
                    slot.ManagedOwnership = false;
                slots[slot.RuntimeSlotId] = slot;
            }
            foreach (BridgeRuntimeSlotRequest request in persisted.QueuedRequests ??
                new List<BridgeRuntimeSlotRequest>()) EnqueueLoaded(request);
        }

        private sealed class SlotStateLease : IDisposable
        {
            private BridgeRuntimeSlotManager owner;
            private IDisposable stateLock;

            internal SlotStateLease(BridgeRuntimeSlotManager owner, IDisposable stateLock)
            {
                this.owner = owner;
                this.stateLock = stateLock;
            }

            public void Dispose()
            {
                if (owner == null) return;
                owner.durableStateDepth--;
                if (owner.durableStateDepth == 0) stateLock?.Dispose();
                owner = null;
                stateLock = null;
            }
        }

        private static bool Compatible(BridgeRuntimeSlotDefinition slot, BridgeOperationCompatibilityKey key)
        {
            return slot != null && key != null && string.Equals(slot.CompatibilityFingerprint,
                CompatibilityDigest(key),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string CompatibilityDigest(BridgeOperationCompatibilityKey key)
        {
            string value = key == null ? string.Empty : key.ToString();
            return value.StartsWith("compat-v1-", StringComparison.Ordinal) ? value.Substring(10) : value;
        }

        private int ActiveProcessCountLocked() => slots.Values.Count(item => item.ActiveProcess);

        private static bool SameProcess(BridgeProcessIdentity first, BridgeProcessIdentity second)
        {
            if (first == null || second == null || first.Pid != second.Pid ||
                !string.Equals(first.ProcessStartIdentity, second.ProcessStartIdentity, StringComparison.Ordinal) ||
                !string.Equals(first.RuntimeSlotId, second.RuntimeSlotId, StringComparison.Ordinal)) return false;
            return (string.IsNullOrEmpty(first.SessionId) || string.IsNullOrEmpty(second.SessionId) ||
                    string.Equals(first.SessionId, second.SessionId, StringComparison.Ordinal)) &&
                (first.LifecycleGeneration == 0 || second.LifecycleGeneration == 0 ||
                    first.LifecycleGeneration == second.LifecycleGeneration) &&
                (string.IsNullOrEmpty(first.LoadedAssemblyFingerprint) ||
                    string.IsNullOrEmpty(second.LoadedAssemblyFingerprint) ||
                    string.Equals(first.LoadedAssemblyFingerprint, second.LoadedAssemblyFingerprint,
                        StringComparison.Ordinal)) &&
                (string.IsNullOrEmpty(first.ExecutablePath) || string.IsNullOrEmpty(second.ExecutablePath) ||
                    string.Equals(first.ExecutablePath, second.ExecutablePath, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(first.ProfileFingerprint) || string.IsNullOrEmpty(second.ProfileFingerprint) ||
                    string.Equals(first.ProfileFingerprint, second.ProfileFingerprint, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SamePath(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
            try { return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool PathsOverlap(string first, string second)
        {
            if (SamePath(first, second)) return true;
            try
            {
                string left = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                string right = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                return left.StartsWith(right, StringComparison.OrdinalIgnoreCase) ||
                    right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string QueueKey(BridgeRuntimeSlotRequest request)
        {
            return BridgeIdentityRules.Normalize(request?.AgentId, "agent") + "\u001f" +
                BridgeIdentityRules.Normalize(request?.ClientInstanceId, "client");
        }

        private static bool HasLease(BridgeRuntimeSlotDefinition slot, string operationId, string agentId,
            string clientInstanceId)
        {
            return (slot.ActiveLeases ?? new List<BridgeRuntimeSlotLease>()).Any(item =>
                string.Equals(item.OperationId, operationId, StringComparison.Ordinal) &&
                string.Equals(item.AgentId, agentId, StringComparison.Ordinal) &&
                string.Equals(item.ClientInstanceId, clientInstanceId, StringComparison.Ordinal));
        }

        private static BridgeRuntimeSlotDefinition Clone(BridgeRuntimeSlotDefinition source)
        {
            return new BridgeRuntimeSlotDefinition
            {
                RuntimeSlotId = source.RuntimeSlotId,
                ManagedProfile = source.ManagedProfile,
                UserDataRoot = source.UserDataRoot,
                ModConfigurationFingerprint = source.ModConfigurationFingerprint,
                DeploymentOverlayRoot = source.DeploymentOverlayRoot,
                CoordinatorRoot = source.CoordinatorRoot,
                ProcessRoot = source.ProcessRoot,
                IpcRoot = source.IpcRoot,
                SaveRoot = source.SaveRoot,
                LogRoot = source.LogRoot,
                EvidenceRoot = source.EvidenceRoot,
                ResourceRoot = source.ResourceRoot,
                ConfigFingerprint = source.ConfigFingerprint,
                ModLoadOrderFingerprint = source.ModLoadOrderFingerprint,
                LifecycleGeneration = source.LifecycleGeneration,
                ManagedOwnership = source.ManagedOwnership,
                ActiveProcess = source.ActiveProcess,
                Process = source.Process == null ? null : new BridgeProcessIdentity
                {
                    Pid = source.Process.Pid,
                    ProcessStartIdentity = source.Process.ProcessStartIdentity,
                    SessionId = source.Process.SessionId,
                    LifecycleGeneration = source.Process.LifecycleGeneration,
                    RuntimeSlotId = source.Process.RuntimeSlotId,
                    LoadedAssemblyFingerprint = source.Process.LoadedAssemblyFingerprint,
                    ExecutablePath = source.Process.ExecutablePath,
                    ProfileFingerprint = source.Process.ProfileFingerprint
                },
                CompatibilityFingerprint = source.CompatibilityFingerprint,
                LastQueueSequence = source.LastQueueSequence,
                ExpectedProcess = CloneProcess(source.ExpectedProcess),
                ExpectedOperationId = source.ExpectedOperationId,
                ExpectedAgentId = source.ExpectedAgentId,
                ExpectedClientInstanceId = source.ExpectedClientInstanceId,
                ActiveLeases = (source.ActiveLeases ?? new List<BridgeRuntimeSlotLease>()).Select(item =>
                    new BridgeRuntimeSlotLease
                    {
                        OperationId = item.OperationId,
                        AgentId = item.AgentId,
                        ClientInstanceId = item.ClientInstanceId
                    }).ToList()
            };
        }

        private static BridgeProcessIdentity CloneProcess(BridgeProcessIdentity source)
        {
            return source == null ? null : new BridgeProcessIdentity
            {
                Pid = source.Pid,
                ProcessStartIdentity = source.ProcessStartIdentity,
                SessionId = source.SessionId,
                LifecycleGeneration = source.LifecycleGeneration,
                RuntimeSlotId = source.RuntimeSlotId,
                LoadedAssemblyFingerprint = source.LoadedAssemblyFingerprint,
                ExecutablePath = source.ExecutablePath,
                ProfileFingerprint = source.ProfileFingerprint
            };
        }

        private static bool IsLiveManagedProcess(BridgeRuntimeSlotDefinition slot)
        {
            BridgeProcessIdentity expected = slot.Process;
            if (expected == null || expected.Pid <= 0 || string.IsNullOrWhiteSpace(expected.ProcessStartIdentity) ||
                string.IsNullOrWhiteSpace(expected.ExecutablePath) ||
                string.IsNullOrWhiteSpace(expected.SessionId) ||
                string.IsNullOrWhiteSpace(expected.LoadedAssemblyFingerprint) ||
                !string.Equals(expected.RuntimeSlotId, slot.RuntimeSlotId, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(slot.LifecycleGeneration) &&
                 !string.Equals(expected.LifecycleGeneration.ToString(), slot.LifecycleGeneration,
                     StringComparison.Ordinal))) return false;
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(expected.Pid))
                {
                    if (process.HasExited) return false;
                    string start = process.StartTime.ToUniversalTime().Ticks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(start, expected.ProcessStartIdentity, StringComparison.Ordinal)) return false;
                    if (!string.IsNullOrWhiteSpace(expected.ProfileFingerprint) &&
                        !string.Equals(expected.ProfileFingerprint,
                            BridgeHashing.Sha256(slot.ManagedProfile ?? string.Empty),
                            StringComparison.OrdinalIgnoreCase)) return false;
                    string executable = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                    return string.Equals(Path.GetFullPath(executable), Path.GetFullPath(expected.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static bool IsLiveExpectedProcess(BridgeRuntimeSlotDefinition slot)
        {
            BridgeProcessIdentity expected = slot.ExpectedProcess;
            if (expected == null || expected.Pid <= 0 || string.IsNullOrWhiteSpace(expected.ProcessStartIdentity) ||
                string.IsNullOrWhiteSpace(expected.ExecutablePath) ||
                !string.Equals(expected.RuntimeSlotId, slot.RuntimeSlotId, StringComparison.Ordinal)) return false;
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(expected.Pid))
                {
                    if (process.HasExited) return false;
                    string start = process.StartTime.ToUniversalTime().Ticks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(start, expected.ProcessStartIdentity, StringComparison.Ordinal)) return false;
                    if (!string.IsNullOrWhiteSpace(expected.ProfileFingerprint) &&
                        !string.Equals(expected.ProfileFingerprint,
                            BridgeHashing.Sha256(slot.ManagedProfile ?? string.Empty),
                            StringComparison.OrdinalIgnoreCase)) return false;
                    string executable = process.MainModule == null ? string.Empty : process.MainModule.FileName;
                    return string.Equals(Path.GetFullPath(executable), Path.GetFullPath(expected.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static bool OwnerMatches(string expectedOperationId, string expectedAgentId,
            string expectedClientInstanceId, string operationId, string agentId, string clientInstanceId)
        {
            bool hasExpected = !string.IsNullOrEmpty(expectedOperationId) ||
                !string.IsNullOrEmpty(expectedAgentId) || !string.IsNullOrEmpty(expectedClientInstanceId);
            return !hasExpected || (string.Equals(expectedOperationId ?? string.Empty, operationId ?? string.Empty,
                StringComparison.Ordinal) && string.Equals(expectedAgentId ?? string.Empty, agentId ?? string.Empty,
                StringComparison.Ordinal) && string.Equals(expectedClientInstanceId ?? string.Empty,
                clientInstanceId ?? string.Empty, StringComparison.Ordinal));
        }
    }
}
