using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RimWorldDevBridge
{
    public enum BridgeRestartPhase
    {
        RUNNING,
        REQUESTED,
        DRAINING,
        DRAINED,
        STOPPING,
        STARTING,
        WAITING_FOR_BRIDGE,
        WAITING_FOR_GAME,
        READY,
        FAILED,
        USER_RESTART_REQUIRED
    }

    [DataContract]
    public sealed class BridgeRestartTicketRecord
    {
        [DataMember(Order = 1)] public string Ticket;
        [DataMember(Order = 2)] public string CycleId;
        [DataMember(Order = 3)] public string AgentId;
        [DataMember(Order = 4)] public string PackageId;
        [DataMember(Order = 5)] public string Reason;
        [DataMember(Order = 6)] public string Readiness;
        [DataMember(Order = 7)] public string SavePolicy;
        [DataMember(Order = 8)] public string RequiredCoreFingerprint;
        [DataMember(Order = 9)] public string RequiredAdapterFingerprint;
        [DataMember(Order = 10)] public string Phase;
        [DataMember(Order = 11)] public string Completion;
        [DataMember(Order = 12)] public DateTime CreatedUtc;
        [DataMember(Order = 13)] public DateTime UpdatedUtc;
        [DataMember(Order = 14)] public string NewPid;
        [DataMember(Order = 15)] public string NewBootId;
        [DataMember(Order = 16)] public string NewSessionId;
        [DataMember(Order = 17)] public string NewTransportGeneration;
        [DataMember(Order = 18)] public string NewCoreFingerprint;
        [DataMember(Order = 19)] public string NewAdapterFingerprint;
        [DataMember(Order = 20)] public bool RequiresWriteReacquire = true;
        [DataMember(Order = 21)] public long NewLifecycleGeneration;
        [DataMember(Order = 22)] public string TargetPostcondition;
        [DataMember(Order = 23)] public bool RequiresNewProcess;
        [DataMember(Order = 24)] public long RequestedLifecycleGeneration;
        [DataMember(Order = 25)] public string RequestedPid;
        [DataMember(Order = 26)] public string RequestedSessionId;
        [DataMember(Order = 27)] public string ReplacementCycleId;
    }

    [DataContract]
    public sealed class BridgeRestartCycleRecord
    {
        [DataMember(Order = 1)] public string CycleId;
        [DataMember(Order = 2)] public string Readiness;
        [DataMember(Order = 3)] public string SavePolicy;
        [DataMember(Order = 4)] public string RequiredCoreFingerprint;
        [DataMember(Order = 5)] public string RequiredAdapterFingerprint;
        [DataMember(Order = 6)] public string Phase;
        [DataMember(Order = 7)] public long BarrierId;
        [DataMember(Order = 8)] public List<string> TicketIds = new List<string>();
        [DataMember(Order = 9)] public DateTime CreatedUtc;
        [DataMember(Order = 10)] public DateTime UpdatedUtc;
        [DataMember(Order = 11)] public string Diagnostics;
        [DataMember(Order = 12)] public bool OwnedProcess;
        [DataMember(Order = 13)] public bool LiveConfirmed;
        [DataMember(Order = 14)] public string OldPid;
        [DataMember(Order = 15)] public string OldBootId;
        [DataMember(Order = 16)] public string NewPid;
        [DataMember(Order = 17)] public string NewBootId;
        [DataMember(Order = 18)] public string NewSessionId;
        [DataMember(Order = 19)] public string NewTransportGeneration;
        [DataMember(Order = 20)] public string NewCoreFingerprint;
        [DataMember(Order = 21)] public string NewAdapterFingerprint;
        [DataMember(Order = 22)] public string CheckpointPath;
        [DataMember(Order = 23)] public int LaunchAttempts;
        [DataMember(Order = 24)] public int MaxLaunchAttempts = 2;
        [DataMember(Order = 25)] public int LaunchBackoffMs = 500;
        [DataMember(Order = 26)] public DateTime NextLaunchUtc;
        [DataMember(Order = 27)] public string TargetPostcondition;
        [DataMember(Order = 28)] public bool RequiresNewProcess;
        [DataMember(Order = 29)] public long RequestedLifecycleGeneration;
        [DataMember(Order = 30)] public string RequestedPid;
        [DataMember(Order = 31)] public string RequestedSessionId;
        [DataMember(Order = 32)] public string RestartReason;
        [DataMember(Order = 33)] public DateTime ProgressDeadlineUtc;
        [DataMember(Order = 34)] public DateTime LastProgressUtc;
        [DataMember(Order = 35)] public string SupersededByCycleId;
        [DataMember(Order = 36)] public long NewLifecycleGeneration;
        [DataMember(Order = 37)] public string OldSessionId;
        [DataMember(Order = 38)] public long OldLifecycleGeneration;
        [DataMember(Order = 39)] public int ProgressTimeoutMs = 120000;
    }

    [DataContract]
    public sealed class BridgeRestartCoordinatorState
    {
        [DataMember(Order = 1)] public int SchemaVersion = 1;
        [DataMember(Order = 2)] public long Sequence;
        [DataMember(Order = 3)] public string InstanceId;
        [DataMember(Order = 4)] public string Phase = BridgeRestartPhase.RUNNING.ToString();
        [DataMember(Order = 5)] public List<BridgeRestartCycleRecord> Cycles = new List<BridgeRestartCycleRecord>();
        [DataMember(Order = 6)] public List<BridgeRestartTicketRecord> Tickets = new List<BridgeRestartTicketRecord>();
        [DataMember(Order = 7)] public string OwnedPid;
        [DataMember(Order = 8)] public string OwnedBootId;
        [DataMember(Order = 9)] public string LastError;
    }

    public sealed class BridgeRestartCoordinatorStateMachine
    {
        private readonly object gate = new object();
        private readonly BridgeRestartCoordinatorState state;

        public BridgeRestartCoordinatorStateMachine(BridgeRestartCoordinatorState initial = null)
        {
            state = initial ?? new BridgeRestartCoordinatorState
            {
                InstanceId = "rc-" + Guid.NewGuid().ToString("N")
            };
            if (state.Cycles == null) state.Cycles = new List<BridgeRestartCycleRecord>();
            if (state.Tickets == null) state.Tickets = new List<BridgeRestartTicketRecord>();
            if (string.IsNullOrEmpty(state.InstanceId)) state.InstanceId = "rc-" + Guid.NewGuid().ToString("N");
            DateTime now = DateTime.UtcNow;
            foreach (BridgeRestartCycleRecord cycle in state.Cycles)
            {
                if (string.IsNullOrEmpty(cycle.TargetPostcondition)) cycle.TargetPostcondition = ReadinessValue(cycle.Readiness);
                if (string.IsNullOrEmpty(cycle.RestartReason)) cycle.RestartReason = "runtime-verification";
                if (cycle.ProgressTimeoutMs <= 0) cycle.ProgressTimeoutMs = 120000;
                if (cycle.LastProgressUtc == default(DateTime)) cycle.LastProgressUtc =
                    cycle.UpdatedUtc == default(DateTime) ? now : cycle.UpdatedUtc;
                if (cycle.ProgressDeadlineUtc == default(DateTime)) cycle.ProgressDeadlineUtc =
                    cycle.LastProgressUtc.AddMilliseconds(ClampProgressTimeout(cycle.ProgressTimeoutMs));
            }
        }

        public BridgeRestartCoordinatorState Snapshot
        {
            get { lock (gate) return Clone(state); }
        }

        public BridgeRestartTicketRecord Request(string agentId, string packageId, string reason,
            string readiness, string savePolicy, string requiredCoreFingerprint,
            string requiredAdapterFingerprint, bool ownedSandbox, bool liveConfirmedAuthorized,
            bool liveConfirmed = false, bool processAlreadyStarted = false,
            int maxLaunchAttempts = 2, int launchBackoffMs = 500,
            string targetPostcondition = null, bool requiresNewProcess = false,
            long requestedLifecycleGeneration = 0, string requestedPid = null,
            string requestedSessionId = null, bool allowSupersede = false,
            int progressTimeoutMs = 120000)
        {
            lock (gate)
            {
                ValidateRequest(readiness, savePolicy);
                string target = ReadinessValue(targetPostcondition) ?? ReadinessValue(readiness);
                string safeReason = SafeReason(reason);
                int boundedProgressTimeout = ClampProgressTimeout(progressTimeoutMs);
                if (liveConfirmed && !liveConfirmedAuthorized)
                {
                    BridgeRestartTicketRecord unauthorized = NewTicket(null, agentId, packageId, reason,
                        readiness, savePolicy, requiredCoreFingerprint, requiredAdapterFingerprint,
                        BridgeRestartPhase.FAILED, "live_confirmed_restart_authorization_required",
                        target, requiresNewProcess, requestedLifecycleGeneration, requestedPid,
                        requestedSessionId);
                    state.Tickets.Add(unauthorized);
                    Touch();
                    return Clone(unauthorized);
                }

                BridgeRestartCycleRecord cycle = state.Cycles.LastOrDefault(item =>
                    IsOpenCompatible(item) && Compatible(item, target, savePolicy,
                        requiredCoreFingerprint, requiredAdapterFingerprint, safeReason,
                        requiresNewProcess, processAlreadyStarted, requestedPid,
                        requestedSessionId, requestedLifecycleGeneration));
                if (cycle == null)
                {
                    BridgeRestartCycleRecord active = state.Cycles.LastOrDefault(IsOpenCycle);
                    BridgeRestartCycleRecord stale = active != null && IsProgressStale(active) ? active : null;
                    if (stale != null && (!allowSupersede || !ownedSandbox))
                    {
                        BridgeRestartTicketRecord staleBlocked = NewTicket(null, agentId, packageId, reason,
                            readiness, savePolicy, requiredCoreFingerprint, requiredAdapterFingerprint,
                            BridgeRestartPhase.FAILED,
                            ownedSandbox ? "restart_cycle_stale_in_progress" :
                                "attached_live_process_requires_operator",
                            target, requiresNewProcess, requestedLifecycleGeneration, requestedPid,
                            requestedSessionId);
                        state.Tickets.Add(staleBlocked);
                        Touch();
                        return Clone(staleBlocked);
                    }
                    if (active != null && stale == null)
                    {
                        BridgeRestartTicketRecord blocked = NewTicket(null, agentId, packageId, reason,
                            readiness, savePolicy, requiredCoreFingerprint, requiredAdapterFingerprint,
                            BridgeRestartPhase.FAILED, "restart_cycle_incompatible_in_progress",
                            target, requiresNewProcess, requestedLifecycleGeneration, requestedPid,
                            requestedSessionId);
                        state.Tickets.Add(blocked);
                        Touch();
                        return Clone(blocked);
                    }
                    cycle = new BridgeRestartCycleRecord
                    {
                        CycleId = "cycle-" + Guid.NewGuid().ToString("N"),
                        Readiness = ReadinessValue(readiness),
                        TargetPostcondition = target,
                        SavePolicy = savePolicy,
                        RequiredCoreFingerprint = requiredCoreFingerprint ?? string.Empty,
                        RequiredAdapterFingerprint = requiredAdapterFingerprint ?? string.Empty,
                        Phase = (processAlreadyStarted ? BridgeRestartPhase.WAITING_FOR_BRIDGE :
                            BridgeRestartPhase.REQUESTED).ToString(),
                        OwnedProcess = ownedSandbox,
                        RequiresNewProcess = requiresNewProcess && !processAlreadyStarted,
                        RequestedLifecycleGeneration = requestedLifecycleGeneration,
                        RequestedPid = Bound(requestedPid, 32),
                        RequestedSessionId = Bound(requestedSessionId, 128),
                        RestartReason = safeReason,
                        LaunchAttempts = processAlreadyStarted ? 1 : 0,
                        MaxLaunchAttempts = ClampAttempts(maxLaunchAttempts),
                        LaunchBackoffMs = ClampBackoff(launchBackoffMs),
                        ProgressTimeoutMs = boundedProgressTimeout,
                        ProgressDeadlineUtc = DateTime.UtcNow.AddMilliseconds(boundedProgressTimeout),
                        LastProgressUtc = DateTime.UtcNow,
                        LiveConfirmed = liveConfirmed,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    state.Cycles.Add(cycle);
                    if (stale != null && allowSupersede && ownedSandbox)
                        Supersede(stale, cycle);
                }
                else
                {
                    cycle.Readiness = MaxReadiness(cycle.Readiness, target);
                    cycle.TargetPostcondition = MaxReadiness(cycle.TargetPostcondition, target);
                    if (string.IsNullOrEmpty(cycle.RequiredCoreFingerprint))
                        cycle.RequiredCoreFingerprint = requiredCoreFingerprint ?? string.Empty;
                    if (string.IsNullOrEmpty(cycle.RequiredAdapterFingerprint))
                        cycle.RequiredAdapterFingerprint = requiredAdapterFingerprint ?? string.Empty;
                }
                BridgeRestartPhase ticketPhase = ParsePhase(cycle.Phase);
                BridgeRestartTicketRecord ticket = NewTicket(cycle.CycleId, agentId, packageId, reason,
                    readiness, savePolicy, requiredCoreFingerprint, requiredAdapterFingerprint,
                    ticketPhase, "restart_requested", target, cycle.RequiresNewProcess || requiresNewProcess,
                    requestedLifecycleGeneration, requestedPid, requestedSessionId);
                cycle.TicketIds.Add(ticket.Ticket);
                state.Tickets.Add(ticket);
                cycle.UpdatedUtc = DateTime.UtcNow;
                Touch();
                return Clone(ticket);
            }
        }

        public BridgeRestartCoordinatorState SetPhase(string cycleId, BridgeRestartPhase phase,
            string diagnostics = null, long barrierId = 0)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) throw new InvalidOperationException("unknown_restart_cycle");
                if (!IsValidTransition(ParsePhase(cycle.Phase), phase))
                    throw new InvalidOperationException("invalid_restart_transition");
                cycle.Phase = phase.ToString();
                if (barrierId > 0) cycle.BarrierId = barrierId;
                cycle.Diagnostics = Bound(diagnostics, 512);
                cycle.UpdatedUtc = DateTime.UtcNow;
                cycle.LastProgressUtc = cycle.UpdatedUtc;
                if (cycle.ProgressTimeoutMs <= 0) cycle.ProgressTimeoutMs = 120000;
                if (phase == BridgeRestartPhase.STARTING || phase == BridgeRestartPhase.WAITING_FOR_BRIDGE ||
                    phase == BridgeRestartPhase.WAITING_FOR_GAME)
                    cycle.ProgressDeadlineUtc = cycle.UpdatedUtc.AddMilliseconds(ClampProgressTimeout(cycle.ProgressTimeoutMs));
                state.Phase = cycle.Phase;
                foreach (BridgeRestartTicketRecord ticket in state.Tickets.Where(item => item.CycleId == cycleId))
                {
                    ticket.Phase = cycle.Phase;
                    ticket.UpdatedUtc = cycle.UpdatedUtc;
                    if (phase == BridgeRestartPhase.READY)
                        ticket.Completion = string.IsNullOrEmpty(cycle.Diagnostics) ?
                            "bridge_ready" : Bound(cycle.Diagnostics, 512);
                    else if (phase == BridgeRestartPhase.FAILED || phase == BridgeRestartPhase.USER_RESTART_REQUIRED)
                        ticket.Completion = cycle.Diagnostics;
                }
                Touch();
                return Clone(state);
            }
        }

        public BridgeRestartCoordinatorState SetBarrierId(string cycleId, long barrierId, string diagnostics = null)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) throw new InvalidOperationException("unknown_restart_cycle");
                if (barrierId > 0) cycle.BarrierId = barrierId;
                cycle.Diagnostics = Bound(diagnostics, 512);
                cycle.UpdatedUtc = DateTime.UtcNow;
                Touch();
                return Clone(state);
            }
        }

        public bool IsProgressExpired(string cycleId, DateTime nowUtc)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                return cycle != null && cycle.ProgressDeadlineUtc != default(DateTime) &&
                    nowUtc >= cycle.ProgressDeadlineUtc;
            }
        }

        public BridgeRestartTicketRecord Ticket(string ticketId)
        {
            lock (gate)
            {
                BridgeRestartTicketRecord ticket = state.Tickets.FirstOrDefault(item =>
                    string.Equals(item.Ticket, ticketId, StringComparison.Ordinal));
                return ticket == null ? null : Clone(ticket);
            }
        }

        public BridgeRestartCycleRecord Cycle(string cycleId)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                return cycle == null ? null : Clone(cycle);
            }
        }

        public void Fail(string cycleId, string diagnostics)
        {
            SetPhase(cycleId, BridgeRestartPhase.FAILED, diagnostics);
        }

        public void PrepareLaunchRetry(string cycleId, int attempts, int maxAttempts,
            int backoffMs, DateTime nextLaunchUtc, string diagnostics)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) return;
                if (!IsValidTransition(ParsePhase(cycle.Phase), BridgeRestartPhase.STARTING))
                    throw new InvalidOperationException("invalid_restart_transition");
                cycle.LaunchAttempts = Math.Max(0, attempts);
                cycle.MaxLaunchAttempts = ClampAttempts(maxAttempts);
                cycle.LaunchBackoffMs = ClampBackoff(backoffMs);
                cycle.NextLaunchUtc = nextLaunchUtc;
                cycle.Phase = BridgeRestartPhase.STARTING.ToString();
                cycle.Diagnostics = Bound(diagnostics, 512);
                cycle.LastProgressUtc = DateTime.UtcNow;
                cycle.ProgressDeadlineUtc = cycle.LastProgressUtc.AddMilliseconds(ClampProgressTimeout(cycle.ProgressTimeoutMs));
                state.Phase = cycle.Phase;
                foreach (BridgeRestartTicketRecord ticket in state.Tickets.Where(item => item.CycleId == cycleId))
                {
                    ticket.Phase = cycle.Phase;
                    ticket.Completion = cycle.Diagnostics;
                    ticket.UpdatedUtc = DateTime.UtcNow;
                }
                Touch();
            }
        }

        public void RecoverStaleOwnership(string diagnostics)
        {
            lock (gate)
            {
                string bounded = Bound(diagnostics, 512);
                foreach (BridgeRestartCycleRecord cycle in state.Cycles)
                {
                    BridgeRestartPhase phase = ParsePhase(cycle.Phase);
                    if (phase == BridgeRestartPhase.READY || phase == BridgeRestartPhase.FAILED ||
                        phase == BridgeRestartPhase.USER_RESTART_REQUIRED) continue;
                    cycle.Phase = BridgeRestartPhase.FAILED.ToString();
                    cycle.Diagnostics = bounded;
                    cycle.UpdatedUtc = DateTime.UtcNow;
                    foreach (BridgeRestartTicketRecord ticket in state.Tickets.Where(item => item.CycleId == cycle.CycleId))
                    {
                        ticket.Phase = cycle.Phase;
                        ticket.Completion = bounded;
                        ticket.UpdatedUtc = cycle.UpdatedUtc;
                    }
                }
                state.OwnedPid = string.Empty;
                state.OwnedBootId = string.Empty;
                state.LastError = bounded;
                Touch();
            }
        }

        public void SetLastError(string diagnostics)
        {
            lock (gate)
            {
                state.LastError = Bound(diagnostics, 512);
                Touch();
            }
        }

        public void ClearOwnedProcess()
        {
            lock (gate)
            {
                state.OwnedPid = string.Empty;
                state.OwnedBootId = string.Empty;
                Touch();
            }
        }

        public void SetCycleIdentity(string cycleId, string oldPid, string oldBootId,
            string oldSessionId = null, long oldLifecycleGeneration = 0)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) return;
                cycle.OldPid = Bound(oldPid, 32);
                cycle.OldBootId = Bound(oldBootId, 128);
                cycle.OldSessionId = Bound(oldSessionId, 128);
                cycle.OldLifecycleGeneration = oldLifecycleGeneration;
                Touch();
            }
        }

        public void SetReadyContext(string cycleId, string pid, string bootId, string session,
            string transportGeneration, string coreFingerprint, string adapterFingerprint,
            long lifecycleGeneration = 0)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) return;
                cycle.NewPid = Bound(pid, 32);
                cycle.NewBootId = Bound(bootId, 128);
                cycle.NewSessionId = Bound(session, 128);
                cycle.NewTransportGeneration = Bound(transportGeneration, 32);
                cycle.NewCoreFingerprint = Bound(coreFingerprint, 128);
                cycle.NewAdapterFingerprint = Bound(adapterFingerprint, 128);
                cycle.NewLifecycleGeneration = lifecycleGeneration;
                foreach (BridgeRestartTicketRecord ticket in state.Tickets.Where(item => item.CycleId == cycleId))
                {
                    ticket.NewPid = cycle.NewPid;
                    ticket.NewBootId = cycle.NewBootId;
                    ticket.NewSessionId = cycle.NewSessionId;
                    ticket.NewTransportGeneration = cycle.NewTransportGeneration;
                    ticket.NewCoreFingerprint = cycle.NewCoreFingerprint;
                    ticket.NewAdapterFingerprint = cycle.NewAdapterFingerprint;
                    ticket.NewLifecycleGeneration = cycle.NewLifecycleGeneration;
                }
                Touch();
            }
        }

        public void SetStartedPid(string cycleId, string pid)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) return;
                cycle.NewPid = Bound(pid, 32);
                Touch();
            }
        }

        public void SetLaunchAttempt(string cycleId, int attempt)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) return;
                cycle.LaunchAttempts = Math.Max(0, attempt);
                cycle.UpdatedUtc = DateTime.UtcNow;
                Touch();
            }
        }

        public void SetCheckpoint(string cycleId, string path)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) return;
                cycle.CheckpointPath = Bound(path, 512);
                Touch();
            }
        }

        public static void WriteAtomic(string path, BridgeRestartCoordinatorState value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temp = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                    typeof(BridgeRestartCoordinatorState));
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None)) serializer.WriteObject(stream, value);
                ReplaceAtomic(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static void ReplaceAtomic(string temporary, string path)
        {
            IOException last = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    return;
                }
                catch (IOException error)
                {
                    last = error;
                    if (attempt < 7) Thread.Sleep(25);
                }
            }
            throw last ?? new IOException("atomic state replacement failed");
        }

        public static BridgeRestartCoordinatorState Read(string path)
        {
            if (!File.Exists(path)) return null;
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                typeof(BridgeRestartCoordinatorState));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return serializer.ReadObject(stream) as BridgeRestartCoordinatorState;
        }

        public static string Secret(string path)
        {
            if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8).Trim();
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            string value = Convert.ToBase64String(bytes);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, value, new UTF8Encoding(false));
            try
            {
                FileSecurity security = new FileSecurity();
                SecurityIdentifier owner = WindowsIdentity.GetCurrent().User;
                security.SetOwner(owner);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.Read | FileSystemRights.Write,
                    AccessControlType.Allow));
                File.SetAccessControl(path, security);
            }
            catch { }
            return value;
        }

        private bool IsOpenCompatible(BridgeRestartCycleRecord cycle)
        {
            return IsOpenCycle(cycle) && !IsProgressStale(cycle);
        }

        private static bool IsOpenCycle(BridgeRestartCycleRecord cycle)
        {
            BridgeRestartPhase phase = ParsePhase(cycle.Phase);
            return phase != BridgeRestartPhase.READY && phase != BridgeRestartPhase.FAILED &&
                phase != BridgeRestartPhase.USER_RESTART_REQUIRED;
        }

        private static bool Compatible(BridgeRestartCycleRecord cycle, string targetPostcondition,
            string savePolicy, string core, string adapter, string reason, bool requiresNewProcess,
            bool processAlreadyStarted, string requestedPid, string requestedSessionId,
            long requestedLifecycleGeneration)
        {
            if (!string.Equals(cycle.SavePolicy, savePolicy, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredCoreFingerprint) && !string.IsNullOrEmpty(core) &&
                !string.Equals(cycle.RequiredCoreFingerprint, core, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredAdapterFingerprint) && !string.IsNullOrEmpty(adapter) &&
                !string.Equals(cycle.RequiredAdapterFingerprint, adapter, StringComparison.OrdinalIgnoreCase)) return false;
            if (ReadinessRank(cycle.TargetPostcondition ?? cycle.Readiness) < ReadinessRank(targetPostcondition)) return false;
            bool cycleRequiresReplacement = cycle.RequiresNewProcess || IsReplacementReason(cycle.RestartReason);
            bool requestRequiresReplacement = requiresNewProcess || IsReplacementReason(reason);
            if (cycleRequiresReplacement != requestRequiresReplacement) return false;
            if (cycleRequiresReplacement && !string.Equals(cycle.RestartReason ?? string.Empty,
                reason ?? string.Empty, StringComparison.Ordinal)) return false;
            bool replacementAlreadyStarted = cycle.RequiresNewProcess && processAlreadyStarted &&
                !string.IsNullOrEmpty(requestedPid) &&
                string.Equals(cycle.NewPid, requestedPid, StringComparison.Ordinal);
            if (requiresNewProcess && !cycle.RequiresNewProcess && !replacementAlreadyStarted) return false;
            if (!string.IsNullOrEmpty(requestedPid) && string.IsNullOrEmpty(cycle.NewPid) &&
                !string.IsNullOrEmpty(cycle.RequestedPid) &&
                !string.Equals(cycle.RequestedPid, requestedPid, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(requestedSessionId) && string.IsNullOrEmpty(cycle.NewSessionId) &&
                !string.IsNullOrEmpty(cycle.RequestedSessionId) &&
                !string.Equals(cycle.RequestedSessionId, requestedSessionId, StringComparison.Ordinal)) return false;
            if (requestedLifecycleGeneration > 0 && cycle.RequestedLifecycleGeneration > 0 &&
                cycle.RequestedLifecycleGeneration != requestedLifecycleGeneration &&
                string.IsNullOrEmpty(cycle.NewPid)) return false;
            return true;
        }

        private static bool IsProgressStale(BridgeRestartCycleRecord cycle)
        {
            return cycle.ProgressDeadlineUtc != default(DateTime) && DateTime.UtcNow >= cycle.ProgressDeadlineUtc;
        }

        private static bool IsReplacementReason(string reason)
        {
            string value = (reason ?? string.Empty).ToLowerInvariant();
            return value.Contains("assembly") || value.Contains("replacement") ||
                value.Contains("new process") || value.Contains("new pid") ||
                value.Contains("new session") || value.Contains("restart-required");
        }

        private void Supersede(BridgeRestartCycleRecord oldCycle, BridgeRestartCycleRecord replacement)
        {
            string diagnostics = "restart_cycle_superseded;replacementCycle=" + replacement.CycleId;
            oldCycle.SupersededByCycleId = replacement.CycleId;
            oldCycle.Phase = BridgeRestartPhase.FAILED.ToString();
            oldCycle.Diagnostics = Bound(diagnostics, 512);
            oldCycle.UpdatedUtc = DateTime.UtcNow;
            foreach (BridgeRestartTicketRecord ticket in state.Tickets.Where(item => item.CycleId == oldCycle.CycleId).ToList())
            {
                oldCycle.TicketIds.Remove(ticket.Ticket);
                replacement.TicketIds.Add(ticket.Ticket);
                ticket.ReplacementCycleId = replacement.CycleId;
                ticket.CycleId = replacement.CycleId;
                ticket.TargetPostcondition = replacement.TargetPostcondition;
                ticket.RequiresNewProcess = replacement.RequiresNewProcess;
                ticket.Phase = replacement.Phase;
                ticket.Completion = Bound(diagnostics, 512);
                ticket.UpdatedUtc = replacement.UpdatedUtc;
            }
        }

        private static string MaxReadiness(string first, string second)
        {
            int left = ReadinessRank(first);
            int right = ReadinessRank(second);
            return right > left ? second : first;
        }

        private static int ReadinessRank(string value)
        {
            if (string.Equals(value, "test_ready", StringComparison.OrdinalIgnoreCase)) return 4;
            if (string.Equals(value, "map", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(value, "game", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }

        private static int ClampProgressTimeout(int value)
        {
            return Math.Max(1000, Math.Min(600000, value <= 0 ? 120000 : value));
        }

        private BridgeRestartCycleRecord FindCycle(string cycleId)
        {
            return state.Cycles.FirstOrDefault(item => string.Equals(item.CycleId, cycleId,
                StringComparison.Ordinal));
        }

        private void Touch()
        {
            state.Sequence++;
            state.Phase = state.Cycles.LastOrDefault()?.Phase ?? BridgeRestartPhase.RUNNING.ToString();
        }

        private static BridgeRestartTicketRecord NewTicket(string cycleId, string agentId, string packageId,
            string reason, string readiness, string savePolicy, string core, string adapter,
            BridgeRestartPhase phase, string completion, string targetPostcondition = null,
            bool requiresNewProcess = false, long requestedLifecycleGeneration = 0,
            string requestedPid = null, string requestedSessionId = null)
        {
            return new BridgeRestartTicketRecord
            {
                Ticket = "ticket-" + Guid.NewGuid().ToString("N"),
                CycleId = cycleId,
                AgentId = Bound(agentId, 128),
                PackageId = Bound(packageId, 128),
                Reason = SafeReason(reason),
                Readiness = ReadinessValue(readiness),
                SavePolicy = savePolicy,
                RequiredCoreFingerprint = Bound(core, 128),
                RequiredAdapterFingerprint = Bound(adapter, 128),
                TargetPostcondition = ReadinessValue(targetPostcondition) ?? ReadinessValue(readiness),
                RequiresNewProcess = requiresNewProcess,
                RequestedLifecycleGeneration = requestedLifecycleGeneration,
                RequestedPid = Bound(requestedPid, 32),
                RequestedSessionId = Bound(requestedSessionId, 128),
                Phase = phase.ToString(),
                Completion = Bound(completion, 512),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static void ValidateRequest(string readiness, string savePolicy)
        {
            if (ReadinessValue(readiness) == null) throw new ArgumentException("invalid_readiness");
            if (!string.Equals(savePolicy, "none", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(savePolicy, "development-copy", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("invalid_save_policy");
        }

        private static string ReadinessValue(string value)
        {
            if (string.Equals(value, "test_ready", StringComparison.OrdinalIgnoreCase)) return "test_ready";
            if (string.Equals(value, "bridge", StringComparison.OrdinalIgnoreCase)) return "bridge";
            if (string.Equals(value, "game", StringComparison.OrdinalIgnoreCase)) return "game";
            if (string.Equals(value, "map", StringComparison.OrdinalIgnoreCase)) return "map";
            return null;
        }

        private static BridgeRestartPhase ParsePhase(string value)
        {
            BridgeRestartPhase result;
            return Enum.TryParse(value, true, out result) ? result : BridgeRestartPhase.FAILED;
        }

        private static bool IsValidTransition(BridgeRestartPhase from, BridgeRestartPhase to)
        {
            if (from == to) return true;
            if (to == BridgeRestartPhase.FAILED || to == BridgeRestartPhase.USER_RESTART_REQUIRED) return true;
            if (from == BridgeRestartPhase.RUNNING && to == BridgeRestartPhase.REQUESTED) return true;
            if (from == BridgeRestartPhase.REQUESTED && to == BridgeRestartPhase.DRAINING) return true;
            if (from == BridgeRestartPhase.REQUESTED && to == BridgeRestartPhase.STARTING) return true;
            if (from == BridgeRestartPhase.DRAINING && to == BridgeRestartPhase.DRAINED) return true;
            if (from == BridgeRestartPhase.DRAINING && to == BridgeRestartPhase.STARTING) return true;
            if (from == BridgeRestartPhase.DRAINED && to == BridgeRestartPhase.STOPPING) return true;
            if (from == BridgeRestartPhase.DRAINED && to == BridgeRestartPhase.STARTING) return true;
            if (from == BridgeRestartPhase.STOPPING && to == BridgeRestartPhase.STARTING) return true;
            if (from == BridgeRestartPhase.STARTING && to == BridgeRestartPhase.WAITING_FOR_BRIDGE) return true;
            if (from == BridgeRestartPhase.WAITING_FOR_BRIDGE &&
                (to == BridgeRestartPhase.STARTING || to == BridgeRestartPhase.WAITING_FOR_GAME ||
                 to == BridgeRestartPhase.READY)) return true;
            if (from == BridgeRestartPhase.WAITING_FOR_GAME &&
                (to == BridgeRestartPhase.STOPPING || to == BridgeRestartPhase.STARTING ||
                 to == BridgeRestartPhase.READY)) return true;
            return false;
        }

        private static string Bound(string value, int length)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= length ? value : value.Substring(0, length);
        }

        private static int ClampAttempts(int value)
        {
            return Math.Max(1, Math.Min(5, value <= 0 ? 2 : value));
        }

        private static int ClampBackoff(int value)
        {
            return Math.Max(0, Math.Min(10000, value));
        }

        private static string SafeReason(string value)
        {
            string result = Bound(value, 256).Replace('\r', ' ').Replace('\n', ' ');
            foreach (string marker in new[] { "token=", "lease=", "secret=", "password=" })
            {
                int start = result.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                int end = result.IndexOf(' ', start);
                if (end < 0) end = result.Length;
                result = result.Substring(0, start) + marker + "[redacted]" + result.Substring(end);
            }
            return result;
        }

        private static BridgeRestartCoordinatorState Clone(BridgeRestartCoordinatorState value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                    typeof(BridgeRestartCoordinatorState));
                serializer.WriteObject(stream, value);
                stream.Position = 0;
                return (BridgeRestartCoordinatorState)serializer.ReadObject(stream);
            }
        }

        private static BridgeRestartCycleRecord Clone(BridgeRestartCycleRecord value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                    typeof(BridgeRestartCycleRecord));
                serializer.WriteObject(stream, value);
                stream.Position = 0;
                return (BridgeRestartCycleRecord)serializer.ReadObject(stream);
            }
        }

        private static BridgeRestartTicketRecord Clone(BridgeRestartTicketRecord value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                    typeof(BridgeRestartTicketRecord));
                serializer.WriteObject(stream, value);
                stream.Position = 0;
                return (BridgeRestartTicketRecord)serializer.ReadObject(stream);
            }
        }
    }
}
