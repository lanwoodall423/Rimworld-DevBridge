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
        }

        public BridgeRestartCoordinatorState Snapshot
        {
            get { lock (gate) return Clone(state); }
        }

        public BridgeRestartTicketRecord Request(string agentId, string packageId, string reason,
            string readiness, string savePolicy, string requiredCoreFingerprint,
            string requiredAdapterFingerprint, bool ownedSandbox, bool liveConfirmedAuthorized,
            bool liveConfirmed = false)
        {
            lock (gate)
            {
                ValidateRequest(readiness, savePolicy);
                if (liveConfirmed && !liveConfirmedAuthorized)
                {
                    BridgeRestartTicketRecord unauthorized = NewTicket(null, agentId, packageId, reason,
                        readiness, savePolicy, requiredCoreFingerprint, requiredAdapterFingerprint,
                        BridgeRestartPhase.FAILED, "live_confirmed_restart_authorization_required");
                    state.Tickets.Add(unauthorized);
                    Touch();
                    return Clone(unauthorized);
                }

                BridgeRestartCycleRecord cycle = state.Cycles.LastOrDefault(item =>
                    IsOpenCompatible(item) && Compatible(item, readiness, savePolicy,
                        requiredCoreFingerprint, requiredAdapterFingerprint));
                if (cycle == null)
                {
                    cycle = new BridgeRestartCycleRecord
                    {
                        CycleId = "cycle-" + Guid.NewGuid().ToString("N"),
                        Readiness = ReadinessValue(readiness),
                        SavePolicy = savePolicy,
                        RequiredCoreFingerprint = requiredCoreFingerprint ?? string.Empty,
                        RequiredAdapterFingerprint = requiredAdapterFingerprint ?? string.Empty,
                        Phase = BridgeRestartPhase.REQUESTED.ToString(),
                        OwnedProcess = ownedSandbox,
                        LiveConfirmed = liveConfirmed,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };
                    state.Cycles.Add(cycle);
                }
                else
                {
                    cycle.Readiness = MaxReadiness(cycle.Readiness, ReadinessValue(readiness));
                    if (string.IsNullOrEmpty(cycle.RequiredCoreFingerprint))
                        cycle.RequiredCoreFingerprint = requiredCoreFingerprint ?? string.Empty;
                    if (string.IsNullOrEmpty(cycle.RequiredAdapterFingerprint))
                        cycle.RequiredAdapterFingerprint = requiredAdapterFingerprint ?? string.Empty;
                }
                BridgeRestartTicketRecord ticket = NewTicket(cycle.CycleId, agentId, packageId, reason,
                    readiness, savePolicy, requiredCoreFingerprint, requiredAdapterFingerprint,
                    BridgeRestartPhase.REQUESTED, "restart_requested");
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
                state.Phase = cycle.Phase;
                foreach (BridgeRestartTicketRecord ticket in state.Tickets.Where(item => item.CycleId == cycleId))
                {
                    ticket.Phase = cycle.Phase;
                    ticket.UpdatedUtc = cycle.UpdatedUtc;
                    if (phase == BridgeRestartPhase.READY)
                        ticket.Completion = "ready_reacquire_write_authority";
                    else if (phase == BridgeRestartPhase.FAILED || phase == BridgeRestartPhase.USER_RESTART_REQUIRED)
                        ticket.Completion = cycle.Diagnostics;
                }
                Touch();
                return Clone(state);
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

        public void SetCycleIdentity(string cycleId, string oldPid, string oldBootId)
        {
            lock (gate)
            {
                BridgeRestartCycleRecord cycle = FindCycle(cycleId);
                if (cycle == null) return;
                cycle.OldPid = Bound(oldPid, 32);
                cycle.OldBootId = Bound(oldBootId, 128);
                Touch();
            }
        }

        public void SetReadyContext(string cycleId, string pid, string bootId, string session,
            string transportGeneration, string coreFingerprint, string adapterFingerprint)
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
                foreach (BridgeRestartTicketRecord ticket in state.Tickets.Where(item => item.CycleId == cycleId))
                {
                    ticket.NewPid = cycle.NewPid;
                    ticket.NewBootId = cycle.NewBootId;
                    ticket.NewSessionId = cycle.NewSessionId;
                    ticket.NewTransportGeneration = cycle.NewTransportGeneration;
                    ticket.NewCoreFingerprint = cycle.NewCoreFingerprint;
                    ticket.NewAdapterFingerprint = cycle.NewAdapterFingerprint;
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
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
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
            BridgeRestartPhase phase = ParsePhase(cycle.Phase);
            if (phase == BridgeRestartPhase.READY || phase == BridgeRestartPhase.FAILED ||
                phase == BridgeRestartPhase.USER_RESTART_REQUIRED) return false;
            return true;
        }

        private static bool Compatible(BridgeRestartCycleRecord cycle, string readiness, string savePolicy,
            string core, string adapter)
        {
            if (!string.Equals(cycle.SavePolicy, savePolicy, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredCoreFingerprint) && !string.IsNullOrEmpty(core) &&
                !string.Equals(cycle.RequiredCoreFingerprint, core, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredAdapterFingerprint) && !string.IsNullOrEmpty(adapter) &&
                !string.Equals(cycle.RequiredAdapterFingerprint, adapter, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static string MaxReadiness(string first, string second)
        {
            int left = ReadinessRank(first);
            int right = ReadinessRank(second);
            return right > left ? second : first;
        }

        private static int ReadinessRank(string value)
        {
            if (string.Equals(value, "map", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(value, "game", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
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
            BridgeRestartPhase phase, string completion)
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
            if (from == BridgeRestartPhase.DRAINING && to == BridgeRestartPhase.DRAINED) return true;
            if (from == BridgeRestartPhase.DRAINED && to == BridgeRestartPhase.STOPPING) return true;
            if (from == BridgeRestartPhase.STOPPING && to == BridgeRestartPhase.STARTING) return true;
            if (from == BridgeRestartPhase.STARTING && to == BridgeRestartPhase.WAITING_FOR_BRIDGE) return true;
            if (from == BridgeRestartPhase.WAITING_FOR_BRIDGE && to == BridgeRestartPhase.WAITING_FOR_GAME) return true;
            if (from == BridgeRestartPhase.WAITING_FOR_GAME && to == BridgeRestartPhase.READY) return true;
            return false;
        }

        private static string Bound(string value, int length)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= length ? value : value.Substring(0, length);
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
