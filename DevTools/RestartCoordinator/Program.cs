using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using RimWorldDevBridge;

namespace RimWorldDevBridge.RestartCoordinator
{
    [DataContract]
    internal sealed class CoordinatorMessage
    {
        [DataMember(Order = 1)] public string Secret;
        [DataMember(Order = 2)] public string Operation;
        [DataMember(Order = 3)] public string Ticket;
        [DataMember(Order = 4)] public string AgentId;
        [DataMember(Order = 5)] public string PackageId;
        [DataMember(Order = 6)] public string Reason;
        [DataMember(Order = 7)] public string Readiness;
        [DataMember(Order = 8)] public string SavePolicy = "none";
        [DataMember(Order = 9)] public string RequiredCoreFingerprint;
        [DataMember(Order = 10)] public string RequiredAdapterFingerprint;
        [DataMember(Order = 11)] public bool LiveConfirmedAuthorized;
        [DataMember(Order = 19)] public bool LiveConfirmed;
        [DataMember(Order = 12)] public string GamePath;
        [DataMember(Order = 13)] public string WorkingDirectory;
        [DataMember(Order = 14)] public string Arguments;
        [DataMember(Order = 15)] public string SavePath;
        [DataMember(Order = 16)] public bool Owned;
        [DataMember(Order = 17)] public int TimeoutMs = 30000;
        [DataMember(Order = 18)] public bool ForceKillTestOnly;
        [DataMember(Order = 20)] public string ModConfiguration;
        [DataMember(Order = 21)] public string Environment;
        [DataMember(Order = 22)] public string LaunchProfile;
        [DataMember(Order = 23)] public string UserDataRoot;
        [DataMember(Order = 24)] public int MaxLaunchAttempts = 2;
        [DataMember(Order = 25)] public int LaunchBackoffMs = 500;
        [DataMember(Order = 26)] public string TargetPostcondition;
        [DataMember(Order = 27)] public bool RequiresNewProcess;
        [DataMember(Order = 28)] public long RequestedLifecycleGeneration;
        [DataMember(Order = 29)] public string RequestedPid;
        [DataMember(Order = 30)] public string RequestedSessionId;
        [DataMember(Order = 31)] public bool AllowSupersede;
    }

    [DataContract]
    internal sealed class CoordinatorResponse
    {
        [DataMember(Order = 1)] public bool Ok;
        [DataMember(Order = 2)] public string Error;
        [DataMember(Order = 3)] public string Phase;
        [DataMember(Order = 4)] public string Ticket;
        [DataMember(Order = 5)] public string CycleId;
        [DataMember(Order = 6)] public string Json;
        [DataMember(Order = 7)] public int ExitCode;
        [DataMember(Order = 8)] public string OwnershipJson;
        [DataMember(Order = 9)] public string CoordinatorIdentity;
    }

    [DataContract]
    internal sealed class LaunchRecord
    {
        [DataMember(Order = 1)] public string GamePath;
        [DataMember(Order = 2)] public string WorkingDirectory;
        [DataMember(Order = 3)] public string Arguments;
        [DataMember(Order = 4)] public string SavePath;
        [DataMember(Order = 5)] public bool Owned;
        [DataMember(Order = 6)] public int ProcessId;
        [DataMember(Order = 7)] public long ProcessStartTimeUtcTicks;
        [DataMember(Order = 8)] public string ModConfiguration;
        [DataMember(Order = 9)] public string Environment;
        [DataMember(Order = 10)] public string LaunchProfile;
        [DataMember(Order = 11)] public string UserDataRoot;
        [DataMember(Order = 12)] public DateTime ProfileValidatedUtc;
        [DataMember(Order = 13)] public string ProfileFingerprint;
        [DataMember(Order = 14)] public int LastExitCode;
        [DataMember(Order = 15)] public bool LastExitCodeKnown;
        [DataMember(Order = 16)] public DateTime LastExitUtc;
    }

    [DataContract]
    internal sealed class CoordinatorOwnership
    {
        [DataMember(Order = 1)] public bool Owned;
        [DataMember(Order = 2)] public bool Running;
        [DataMember(Order = 3)] public int ProcessId;
        [DataMember(Order = 4)] public long ProcessStartTimeUtcTicks;
        [DataMember(Order = 5)] public string BootId;
        [DataMember(Order = 6)] public string LaunchProfile;
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Dictionary<string, string> options = Parse(args);
                string root = Required(options, "root");
                string operation = options.ContainsKey("serve") ? "serve" :
                    options.ContainsKey("operation") ? options["operation"] :
                    (args.Length == 0 ? "serve" : args[0].TrimStart('-').ToLowerInvariant());
                CoordinatorHost host = new CoordinatorHost(root,
                    options.ContainsKey("user-root") ? options["user-root"] : root,
                    options.ContainsKey("bridge-root") ? options["bridge-root"] : string.Empty,
                    options.ContainsKey("force-kill-test-only"), operation == "serve");
                if (operation == "serve") return host.Serve();
                CoordinatorMessage message = Message(operation, options);
                CoordinatorResponse response = host.Call(message);
                Console.WriteLine(Json(response));
                return response.ExitCode;
            }
            catch (Exception exception)
            {
                Console.WriteLine(Json(new CoordinatorResponse
                {
                    Ok = false,
                    Error = exception.GetType().Name + ":" + exception.Message,
                    ExitCode = 2
                }));
                return 2;
            }
        }

        private static CoordinatorMessage Message(string operation, Dictionary<string, string> options)
        {
            return new CoordinatorMessage
            {
                Secret = options.ContainsKey("secret") ? options["secret"] : string.Empty,
                Operation = operation,
                Ticket = Get(options, "ticket"),
                AgentId = Get(options, "agent-id"),
                PackageId = Get(options, "package-id"),
                Reason = Get(options, "reason"),
                Readiness = Get(options, "readiness", "bridge"),
                SavePolicy = Get(options, "save-policy", "none"),
                RequiredCoreFingerprint = Get(options, "required-core-fingerprint"),
                RequiredAdapterFingerprint = Get(options, "required-adapter-fingerprint"),
                LiveConfirmedAuthorized = options.ContainsKey("authorize-live-confirmed"),
                LiveConfirmed = options.ContainsKey("live-confirmed"),
                GamePath = Get(options, "game-path"),
                WorkingDirectory = Get(options, "working-directory"),
                Arguments = Get(options, "arguments"),
                SavePath = Get(options, "save-path"),
                Owned = !options.ContainsKey("attached"),
                TimeoutMs = ParseInt(Get(options, "timeout-ms", "30000"), 30000),
                ForceKillTestOnly = options.ContainsKey("force-kill-test-only"),
                ModConfiguration = Get(options, "mod-configuration"),
                Environment = Get(options, "environment"),
                LaunchProfile = Get(options, "launch-profile"),
                UserDataRoot = Get(options, "user-data-root"),
                MaxLaunchAttempts = ParseInt(Get(options, "max-launch-attempts", "2"), 2),
                LaunchBackoffMs = ParseInt(Get(options, "launch-backoff-ms", "500"), 500),
                TargetPostcondition = Get(options, "target-postcondition"),
                RequiresNewProcess = options.ContainsKey("requires-new-process"),
                RequestedLifecycleGeneration = ParseLong(Get(options, "requested-lifecycle-generation", "0"), 0),
                RequestedPid = Get(options, "requested-pid"),
                RequestedSessionId = Get(options, "requested-session-id"),
                AllowSupersede = options.ContainsKey("allow-supersede")
            };
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
                string key = arg.Substring(2).ToLowerInvariant();
                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    result[key] = args[++index];
                else result[key] = "true";
            }
            return result;
        }

        private static string Required(Dictionary<string, string> values, string name)
        {
            string value;
            if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("missing --" + name);
            return value;
        }

        private static string Get(Dictionary<string, string> values, string name, string fallback = null)
        {
            string value;
            return values.TryGetValue(name, out value) ? value : fallback;
        }

        private static int ParseInt(string value, int fallback)
        {
            int result;
            return int.TryParse(value, out result) ? result : fallback;
        }

        private static long ParseLong(string value, long fallback)
        {
            long result;
            return long.TryParse(value, out result) ? result : fallback;
        }

        internal static string Json(object value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(value.GetType()).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        internal static string CoordinatorIdentity()
        {
            string path = typeof(Program).Assembly.Location;
            string hash;
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            return typeof(Program).Assembly.GetName().Name + "|" +
                typeof(Program).Assembly.GetName().Version + "|" + hash;
        }

        internal static T FromJson<T>(string json)
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }

    internal sealed class CoordinatorHost
    {
        private const int MaximumMessageBytes = 65536;
        private readonly string root;
        private readonly string userRoot;
        private readonly string bridgeRoot;
        private readonly string statePath;
        private readonly string secretPath;
        private readonly string journalPath;
        private readonly string pipeName;
        private readonly bool forceKillTestOnly;
        private readonly bool acquireWriterLock;
        private readonly object gate = new object();
        private BridgeRestartCoordinatorStateMachine machine;
        private FileStream lockStream;
        private string secret;
        private LaunchRecord launchRecord;
        private Process monitoredProcess;

        internal CoordinatorHost(string root, string userRoot, string bridgeRoot, bool forceKillTestOnly,
            bool acquireWriterLock)
        {
            this.root = Path.GetFullPath(root);
            this.userRoot = Path.GetFullPath(userRoot);
            this.bridgeRoot = string.IsNullOrEmpty(bridgeRoot) ? string.Empty : Path.GetFullPath(bridgeRoot);
            this.forceKillTestOnly = forceKillTestOnly;
            this.acquireWriterLock = acquireWriterLock;
            Directory.CreateDirectory(this.root);
            statePath = Path.Combine(this.root, "state.json");
            secretPath = Path.Combine(this.root, "secret.txt");
            journalPath = Path.Combine(this.root, "journal.log");
            pipeName = PipeName(this.root);
        }

        internal int Serve()
        {
            if (acquireWriterLock) AcquireLock();
            Load();
            while (true)
            {
                using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName,
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                {
                    pipe.WaitForConnection();
                    string request = ReadBounded(pipe);
                    CoordinatorResponse response;
                    try
                    {
                        CoordinatorMessage message = Program.FromJson<CoordinatorMessage>(request);
                        response = Handle(message);
                    }
                    catch (Exception exception)
                    {
                        response = new CoordinatorResponse { Ok = false, Error = exception.Message, ExitCode = 2 };
                    }
                    response.CoordinatorIdentity = Program.CoordinatorIdentity();
                    byte[] bytes = Encoding.UTF8.GetBytes(Program.Json(response));
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();
                }
            }
        }

        internal CoordinatorResponse Call(CoordinatorMessage message)
        {
            secret = BridgeRestartCoordinatorStateMachine.Secret(secretPath);
            if (string.IsNullOrEmpty(message.Secret)) message.Secret = secret;
            string json = Program.Json(message);
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            {
                pipe.Connect(Math.Max(100, message.TimeoutMs));
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                return Program.FromJson<CoordinatorResponse>(ReadBounded(pipe));
            }
        }

        private CoordinatorResponse Handle(CoordinatorMessage message)
        {
            if (message == null || !SecureEquals(secret, message.Secret))
                return new CoordinatorResponse { Ok = false, Error = "coordinator_authentication_failed", ExitCode = 3 };
            lock (gate)
            {
                RecoverStaleOwnedProcess();
                switch ((message.Operation ?? string.Empty).ToLowerInvariant())
                {
                    case "request": return Request(message);
                    case "status": return Status(message);
                    case "wait": return Wait(message);
                    case "register": return Register(message);
                    case "launch": return Launch(message);
                    case "ensure": return Ensure(message);
                    case "heartbeat": return Heartbeat();
                    default: return new CoordinatorResponse { Ok = false, Error = "unknown_coordinator_operation", ExitCode = 2 };
                }
            }
        }

        private CoordinatorResponse Request(CoordinatorMessage message)
        {
            return Request(message, false, message.RequiresNewProcess);
        }

        private CoordinatorResponse Request(CoordinatorMessage message, bool processAlreadyStarted)
        {
            return Request(message, processAlreadyStarted, message.RequiresNewProcess);
        }

        private CoordinatorResponse Request(CoordinatorMessage message, bool processAlreadyStarted,
            bool requiresNewProcess)
        {
            bool owned = launchRecord != null && launchRecord.Owned;
            BridgeRestartTicketRecord ticket = machine.Request(message.AgentId, message.PackageId,
                message.Reason, message.Readiness, message.SavePolicy, message.RequiredCoreFingerprint,
                message.RequiredAdapterFingerprint, owned, message.LiveConfirmedAuthorized,
                message.LiveConfirmed, processAlreadyStarted, message.MaxLaunchAttempts,
                message.LaunchBackoffMs, message.TargetPostcondition, requiresNewProcess,
                message.RequestedLifecycleGeneration, message.RequestedPid, message.RequestedSessionId,
                message.AllowSupersede, message.TimeoutMs);
            Persist("request " + ticket.Ticket);
            return TicketResponse(ticket);
        }

        private CoordinatorResponse Status(CoordinatorMessage message)
        {
            Pump();
            BridgeRestartTicketRecord ticket = string.IsNullOrEmpty(message.Ticket) ? null : machine.Ticket(message.Ticket);
            string terminalError = TerminalError(ticket);
            return new CoordinatorResponse
            {
                Ok = ticket != null && string.IsNullOrWhiteSpace(terminalError),
                Error = ticket == null ? "unknown_restart_ticket" : terminalError,
                Ticket = ticket?.Ticket,
                CycleId = ticket?.CycleId,
                Phase = ticket?.Phase,
                Json = Program.Json(machine.Snapshot),
                OwnershipJson = Program.Json(GetOwnership()),
                ExitCode = ticket == null ? 2 : 0
            };
        }

        private CoordinatorResponse Heartbeat()
        {
            Pump();
            return new CoordinatorResponse
            {
                Ok = true,
                Phase = machine.Snapshot.Phase.ToString(),
                Json = Program.Json(machine.Snapshot),
                OwnershipJson = Program.Json(GetOwnership()),
                ExitCode = 0
            };
        }

        private CoordinatorResponse Wait(CoordinatorMessage message)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, message.TimeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                Pump();
                BridgeRestartTicketRecord ticket = machine.Ticket(message.Ticket);
                if (ticket == null) return new CoordinatorResponse { Ok = false, Error = "unknown_restart_ticket", ExitCode = 2 };
                BridgeRestartPhase phase = (BridgeRestartPhase)Enum.Parse(typeof(BridgeRestartPhase), ticket.Phase);
                if (phase == BridgeRestartPhase.READY || phase == BridgeRestartPhase.FAILED ||
                    phase == BridgeRestartPhase.USER_RESTART_REQUIRED)
                    return TicketResponse(ticket);
                Thread.Sleep(250);
            }
            BridgeRestartTicketRecord timeout = machine.Ticket(message.Ticket);
            string timeoutError = timeout != null &&
                (timeout.Phase == BridgeRestartPhase.STARTING.ToString() ||
                 timeout.Phase == BridgeRestartPhase.WAITING_FOR_BRIDGE.ToString()) ?
                "bridge_handshake_timeout" : "coordinator_wait_timeout";
            return TicketResponse(timeout, timeoutError);
        }

        private CoordinatorResponse Ensure(CoordinatorMessage message)
        {
            if (launchRecord != null && !launchRecord.Owned)
            {
                return new CoordinatorResponse
                {
                    Ok = false,
                    Error = "attached_live_process_requires_operator",
                    Phase = BridgeRestartPhase.USER_RESTART_REQUIRED.ToString(),
                    OwnershipJson = Program.Json(GetOwnership()),
                    ExitCode = 4
                };
            }
            if (launchRecord == null)
            {
                if (!message.Owned)
                {
                    return new CoordinatorResponse
                    {
                        Ok = false,
                        Error = "attached_live_process_requires_operator",
                        Phase = BridgeRestartPhase.USER_RESTART_REQUIRED.ToString(),
                        OwnershipJson = Program.Json(GetOwnership()),
                        ExitCode = 4
                    };
                }
                CoordinatorResponse launched = Launch(message);
                if (!launched.Ok) return launched;
                return Request(message, true, false);
            }
            bool processAlreadyStarted = ActiveCycleUsesCurrentLaunch();
            bool requiresNewProcess = message.RequiresNewProcess || !processAlreadyStarted;
            return Request(message, processAlreadyStarted, requiresNewProcess);
        }

        private bool ActiveCycleUsesCurrentLaunch()
        {
            if (launchRecord == null || !launchRecord.Owned) return false;
            string pid = launchRecord.ProcessId > 0 ?
                launchRecord.ProcessId.ToString(CultureInfo.InvariantCulture) : null;
            return machine.Snapshot.Cycles.Any(c =>
            {
                BridgeRestartPhase phase = ParsePhase(c.Phase);
                bool open = phase != BridgeRestartPhase.READY && phase != BridgeRestartPhase.FAILED &&
                    phase != BridgeRestartPhase.USER_RESTART_REQUIRED;
                if (!open || !c.OwnedProcess) return false;
                if (!string.IsNullOrEmpty(pid) && string.Equals(c.NewPid, pid, StringComparison.Ordinal)) return true;
                // A STARTING cycle may intentionally have no PID during bounded retry backoff.
                return launchRecord.ProcessId <= 0 && phase == BridgeRestartPhase.STARTING;
            });
        }

        private CoordinatorResponse Register(CoordinatorMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.GamePath) || !File.Exists(message.GamePath))
                return new CoordinatorResponse { Ok = false, Error = "validated_game_executable_required", ExitCode = 2 };
            string working = string.IsNullOrWhiteSpace(message.WorkingDirectory) ?
                Path.GetDirectoryName(Path.GetFullPath(message.GamePath)) : Path.GetFullPath(message.WorkingDirectory);
            if (!Directory.Exists(working)) return new CoordinatorResponse { Ok = false, Error = "working_directory_missing", ExitCode = 2 };
            string userDataRoot = string.IsNullOrWhiteSpace(message.UserDataRoot) ? userRoot :
                Path.GetFullPath(message.UserDataRoot);
            if (!Directory.Exists(userDataRoot)) return new CoordinatorResponse { Ok = false, Error = "user_data_root_missing", ExitCode = 2 };
            launchRecord = new LaunchRecord
            {
                GamePath = Path.GetFullPath(message.GamePath),
                WorkingDirectory = working,
                Arguments = message.Arguments ?? string.Empty,
                SavePath = message.SavePath,
                Owned = message.Owned,
                ModConfiguration = Limit(message.ModConfiguration, 2048),
                Environment = Limit(message.Environment, 8192),
                LaunchProfile = Limit(message.LaunchProfile, 256),
                UserDataRoot = userDataRoot,
                ProfileValidatedUtc = DateTime.UtcNow
            };
            launchRecord.ProfileFingerprint = ProfileFingerprint(launchRecord);
            monitoredProcess = null;
            Persist("register launch");
            return new CoordinatorResponse
            {
                Ok = true,
                Json = Program.Json(launchRecord),
                OwnershipJson = Program.Json(GetOwnership())
            };
        }

        private CoordinatorResponse Launch(CoordinatorMessage message)
        {
            if (!message.Owned)
                return new CoordinatorResponse { Ok = false, Error = "attached_process_cannot_be_launched", ExitCode = 4 };
            if (!string.Equals(message.LaunchProfile, "managed-test", StringComparison.OrdinalIgnoreCase))
                return new CoordinatorResponse { Ok = false, Error = "validated_launch_profile_required", ExitCode = 2 };
            CoordinatorResponse registered = Register(message);
            if (!registered.Ok) return registered;
            Process process = StartOwned();
            monitoredProcess = process;
            launchRecord.ProcessId = process.Id;
            launchRecord.ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            launchRecord.LastExitCodeKnown = false;
            launchRecord.LastExitCode = 0;
            launchRecord.LastExitUtc = default(DateTime);
            Persist("launch pid=" + process.Id);
            return new CoordinatorResponse
            {
                Ok = true,
                Json = Program.Json(launchRecord),
                OwnershipJson = Program.Json(GetOwnership()),
                ExitCode = 0
            };
        }

        private void Pump()
        {
            BridgeRestartCoordinatorState snapshot = machine.Snapshot;
            BridgeRestartCycleRecord cycle = snapshot.Cycles.LastOrDefault(item =>
                item.Phase != BridgeRestartPhase.READY.ToString() &&
                item.Phase != BridgeRestartPhase.FAILED.ToString() &&
                item.Phase != BridgeRestartPhase.USER_RESTART_REQUIRED.ToString());
            if (cycle == null) return;
            BridgeRestartPhase phase = (BridgeRestartPhase)Enum.Parse(typeof(BridgeRestartPhase), cycle.Phase);
            switch (phase)
            {
                case BridgeRestartPhase.REQUESTED:
                    machine.SetPhase(cycle.CycleId, BridgeRestartPhase.DRAINING);
                    Persist("draining cycle=" + cycle.CycleId);
                    break;
                case BridgeRestartPhase.DRAINING:
                    Dictionary<string, string> beforeDrain = ReadStatus();
                    if (string.IsNullOrEmpty(cycle.OldPid) && string.IsNullOrEmpty(cycle.OldBootId))
                    {
                        machine.SetCycleIdentity(cycle.CycleId, Get(beforeDrain, "processId"),
                            Get(beforeDrain, "bootId"), Get(beforeDrain, "session"),
                            ParseLong(Get(beforeDrain, "lifecycleGeneration"), 0));
                        cycle = machine.Cycle(cycle.CycleId);
                    }
                    string observedPid = Get(beforeDrain, "processId");
                    bool observedProcessRunning = IsProcessRunning(observedPid);
                    bool ownedObservedProcess = launchRecord != null && launchRecord.Owned &&
                        observedPid == launchRecord.ProcessId.ToString() && observedProcessRunning;
                    bool bridgeActive = string.Equals(Get(beforeDrain, "bridge"), "ON",
                        StringComparison.OrdinalIgnoreCase);
                    if (!observedProcessRunning || (ownedObservedProcess && !bridgeActive))
                    {
                        machine.SetPhase(cycle.CycleId, BridgeRestartPhase.DRAINED,
                            "no_active_bridge_to_drain");
                        Persist("drained without active bridge");
                        break;
                    }
                    if (cycle.BarrierId <= 0)
                    {
                        if (TryBridge("RESTART_DRAIN", cycle, out string beginDrainResponse))
                        {
                            long barrierId = ParseLong(ResponseValue(beginDrainResponse, "barrierId"));
                            if (barrierId > 0)
                            {
                                machine.SetBarrierId(cycle.CycleId, barrierId, beginDrainResponse);
                                Persist("restart drain barrier=" + barrierId);
                            }
                        }
                        break;
                    }
                    if (TryBridge("RESTART_DRAIN_STATUS", cycle, out string drainResponse) &&
                        drainResponse.IndexOf("drained=true", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        machine.SetPhase(cycle.CycleId, BridgeRestartPhase.DRAINED, drainResponse);
                        Persist("drained cycle=" + cycle.CycleId);
                    }
                    break;
                case BridgeRestartPhase.DRAINED:
                    if (cycle.SavePolicy == "development-copy" && !CreateDevelopmentCopy(cycle)) break;
                    if (launchRecord == null || !launchRecord.Owned)
                    {
                        machine.SetPhase(cycle.CycleId, BridgeRestartPhase.USER_RESTART_REQUIRED,
                            "attached_live_process_requires_operator");
                        Persist("user restart required");
                    }
                    else
                    {
                        machine.SetPhase(cycle.CycleId, BridgeRestartPhase.STOPPING);
                        Persist("stopping cycle=" + cycle.CycleId);
                    }
                    break;
                case BridgeRestartPhase.STOPPING:
                    StopOwned(cycle);
                    break;
                case BridgeRestartPhase.STARTING:
                    StartOwnedForCycle(cycle);
                    break;
                case BridgeRestartPhase.WAITING_FOR_BRIDGE:
                    if (!TryGetOwnedProcess(out Process waitingProcess))
                    {
                        HandleManagedProcessExit(cycle);
                        break;
                    }
                    if (ReadyForBridge(cycle, out Dictionary<string, string> bridgeStatus))
                    {
                        machine.SetReadyContext(cycle.CycleId, Get(bridgeStatus, "processId"),
                            Get(bridgeStatus, "bootId"), Get(bridgeStatus, "session"),
                            Get(bridgeStatus, "transportGeneration"), Get(bridgeStatus, "coreFingerprint"),
                            cycle.RequiredAdapterFingerprint,
                            ParseLong(Get(bridgeStatus, "lifecycleGeneration"), 0));
                        if (string.Equals(cycle.Readiness, "bridge", StringComparison.OrdinalIgnoreCase))
                        {
                            machine.SetPhase(cycle.CycleId, BridgeRestartPhase.READY,
                                "bridge_ready");
                            Persist("bridge ready cycle=" + cycle.CycleId);
                        }
                        else
                        {
                            machine.SetPhase(cycle.CycleId, BridgeRestartPhase.WAITING_FOR_GAME);
                            Persist("bridge ready; waiting for game cycle=" + cycle.CycleId);
                        }
                    }
                    break;
                case BridgeRestartPhase.WAITING_FOR_GAME:
                    if (machine.IsProgressExpired(cycle.CycleId, DateTime.UtcNow))
                    {
                        if (!TryGetOwnedProcess(out Process stalledProcess))
                        {
                            HandleManagedProcessExit(cycle);
                        }
                        else
                        {
                            machine.SetPhase(cycle.CycleId, BridgeRestartPhase.STOPPING,
                                "bridge_handshake_timeout;managed_launch_retrying;wait_for_game_timeout");
                            Persist("waiting for game watchdog stopping owned process");
                        }
                        break;
                    }
                    if (ReadyForGame(cycle))
                    {
                        machine.SetPhase(cycle.CycleId, BridgeRestartPhase.READY,
                            "fresh_context_ready_reacquire_write_authority");
                        Persist("ready cycle=" + cycle.CycleId);
                    }
                    break;
            }
        }

        private void StopOwned(BridgeRestartCycleRecord cycle)
        {
            try
            {
                Process process = null;
                if (launchRecord != null && launchRecord.ProcessId != 0 &&
                    IsProcessRunning(launchRecord.ProcessId.ToString()))
                    process = Process.GetProcessById(launchRecord.ProcessId);
                if (process != null && !process.HasExited)
                {
                    if (!OwnsProcess(process))
                    {
                        machine.SetPhase(cycle.CycleId, BridgeRestartPhase.USER_RESTART_REQUIRED,
                            "attached_live_process_requires_operator;owned_process_identity_mismatch");
                        Persist("attached live process identity mismatch");
                        return;
                    }
                    process.CloseMainWindow();
                    if (!process.WaitForExit(15000) && forceKillTestOnly) process.Kill();
                    if (!process.HasExited)
                    {
                        machine.Fail(cycle.CycleId, "managed_launch_failed;managed_process_stop_timeout");
                        Persist("stop failed");
                        return;
                    }
                }
                ClearLaunchProcessIdentity();
                machine.SetPhase(cycle.CycleId, BridgeRestartPhase.STARTING);
                Persist("stopped cycle=" + cycle.CycleId);
            }
            catch (ArgumentException)
            {
                ClearLaunchProcessIdentity();
                machine.SetPhase(cycle.CycleId, BridgeRestartPhase.STARTING);
                Persist("owned process exited before stop");
            }
            catch (Exception exception)
            {
                machine.Fail(cycle.CycleId, "stop_failed:" + exception.GetType().Name);
                Persist("stop exception");
            }
        }

        private bool CreateDevelopmentCopy(BridgeRestartCycleRecord cycle)
        {
            if (!string.IsNullOrEmpty(cycle.CheckpointPath)) return true;
            if (launchRecord == null || string.IsNullOrWhiteSpace(launchRecord.SavePath) ||
                !File.Exists(launchRecord.SavePath))
            {
                machine.Fail(cycle.CycleId, "development_copy_save_missing");
                Persist("development copy missing");
                return false;
            }
            string target = launchRecord.SavePath + ".devbridge." + cycle.CycleId + ".copy";
            if (File.Exists(target))
            {
                machine.Fail(cycle.CycleId, "development_copy_target_exists");
                Persist("development copy target exists");
                return false;
            }
            File.Copy(launchRecord.SavePath, target, false);
            machine.SetCheckpoint(cycle.CycleId, target);
            Persist("development copy=" + target);
            return true;
        }

        private void StartOwnedForCycle(BridgeRestartCycleRecord cycle)
        {
            if (cycle.NextLaunchUtc > DateTime.UtcNow) return;
            int attempt = cycle.LaunchAttempts + 1;
            Process startedProcess = null;
            try
            {
                machine.SetLaunchAttempt(cycle.CycleId, attempt);
                startedProcess = StartOwned();
                monitoredProcess = startedProcess;
                launchRecord.ProcessId = startedProcess.Id;
                launchRecord.ProcessStartTimeUtcTicks = startedProcess.StartTime.ToUniversalTime().Ticks;
                launchRecord.LastExitCodeKnown = false;
                launchRecord.LastExitCode = 0;
                launchRecord.LastExitUtc = default(DateTime);
                machine.SetStartedPid(cycle.CycleId, startedProcess.Id.ToString());
                machine.SetPhase(cycle.CycleId, BridgeRestartPhase.WAITING_FOR_BRIDGE);
                Persist("started pid=" + startedProcess.Id);
            }
            catch (Exception exception)
            {
                CloseStartedProcess(startedProcess);
                HandleManagedLaunchFailure(cycle, "launch_profile_invalid:" + exception.GetType().Name);
            }
        }

        private static void CloseStartedProcess(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    process.WaitForExit(5000);
                }
            }
            catch { }
            try { process.Dispose(); } catch { }
        }

        private void HandleManagedProcessExit(BridgeRestartCycleRecord cycle)
        {
            string diagnostics = "managed_process_exited_before_ready;" + ExitDiagnostics();
            ClearLaunchProcessIdentity();
            if (cycle.LaunchAttempts < cycle.MaxLaunchAttempts)
            {
                DateTime next = DateTime.UtcNow.AddMilliseconds(cycle.LaunchBackoffMs);
                machine.PrepareLaunchRetry(cycle.CycleId, cycle.LaunchAttempts, cycle.MaxLaunchAttempts,
                    cycle.LaunchBackoffMs, next, "managed_launch_retrying;" + diagnostics);
                Persist("managed launch retrying attempt=" + (cycle.LaunchAttempts + 1));
                return;
            }
            machine.Fail(cycle.CycleId, "managed_launch_failed;" + diagnostics);
            Persist("managed launch failed;" + diagnostics);
        }

        private void HandleManagedLaunchFailure(BridgeRestartCycleRecord cycle, string failure)
        {
            ClearLaunchProcessIdentity();
            if (cycle.LaunchAttempts < cycle.MaxLaunchAttempts)
            {
                DateTime next = DateTime.UtcNow.AddMilliseconds(cycle.LaunchBackoffMs);
                machine.PrepareLaunchRetry(cycle.CycleId, cycle.LaunchAttempts, cycle.MaxLaunchAttempts,
                    cycle.LaunchBackoffMs, next, "managed_launch_retrying;" + failure);
                Persist("managed launch retrying;" + failure);
                return;
            }
            machine.Fail(cycle.CycleId, "managed_launch_failed;" + failure);
            Persist("managed launch failed;" + failure);
        }

        private string ExitDiagnostics()
        {
            string exit = launchRecord != null && launchRecord.LastExitCodeKnown ?
                launchRecord.LastExitCode.ToString() : "unknown";
            string profile = launchRecord == null ? "unknown" :
                Path.GetFileName(launchRecord.GamePath ?? string.Empty);
            return "exitCode=" + Limit(exit, 32) + ";profile=" + Limit(profile, 128);
        }

        private void ClearLaunchProcessIdentity()
        {
            if (launchRecord != null)
            {
                launchRecord.ProcessId = 0;
                launchRecord.ProcessStartTimeUtcTicks = 0;
            }
            if (monitoredProcess != null)
            {
                monitoredProcess.Dispose();
                monitoredProcess = null;
            }
            machine.ClearOwnedProcess();
        }

        private bool TryGetOwnedProcess(out Process process)
        {
            process = null;
            if (launchRecord == null || !launchRecord.Owned || launchRecord.ProcessId <= 0) return false;
            if (monitoredProcess != null && monitoredProcess.Id == launchRecord.ProcessId)
            {
                if (monitoredProcess.HasExited)
                {
                    CaptureExitCode(monitoredProcess);
                    return false;
                }
                if (!OwnsProcess(monitoredProcess)) return false;
                process = monitoredProcess;
                return true;
            }
            try
            {
                Process candidate = Process.GetProcessById(launchRecord.ProcessId);
                if (candidate.HasExited)
                {
                    CaptureExitCode(candidate);
                    candidate.Dispose();
                    return false;
                }
                if (!OwnsProcess(candidate))
                {
                    candidate.Dispose();
                    return false;
                }
                monitoredProcess = candidate;
                process = candidate;
                return true;
            }
            catch (ArgumentException) { return false; }
            catch { return false; }
        }

        private void RecoverStaleOwnedProcess()
        {
            if (launchRecord == null || !launchRecord.Owned) return;
            BridgeRestartCoordinatorState snapshot = machine.Snapshot;
            BridgeRestartCycleRecord active = snapshot.Cycles.LastOrDefault(item =>
                item.Phase != BridgeRestartPhase.READY.ToString() &&
                item.Phase != BridgeRestartPhase.FAILED.ToString() &&
                item.Phase != BridgeRestartPhase.USER_RESTART_REQUIRED.ToString());
            if (launchRecord.ProcessId <= 0)
            {
                // A retry deliberately clears the PID while waiting for its backoff. Do not
                // reinterpret that expected gap as another exit on every status poll.
                if (active != null && active.Phase == BridgeRestartPhase.STARTING.ToString()) return;
                if (active != null && (active.Phase == BridgeRestartPhase.WAITING_FOR_BRIDGE.ToString() ||
                    active.Phase == BridgeRestartPhase.WAITING_FOR_GAME.ToString()))
                    HandleManagedProcessExit(active);
                return;
            }
            if (TryGetOwnedProcess(out Process ownedProcess)) return;

            bool liveIdentityConflict = false;
            try
            {
                Process candidate = Process.GetProcessById(launchRecord.ProcessId);
                if (!candidate.HasExited)
                {
                    liveIdentityConflict = IsExpectedExecutable(candidate);
                    candidate.Dispose();
                }
            }
            catch { }

            if (liveIdentityConflict)
            {
                ClearLaunchProcessIdentity();
                launchRecord.Owned = false;
                string diagnostics = "attached_live_process_requires_operator;pid_reuse_or_external_process";
                if (active != null) machine.SetPhase(active.CycleId, BridgeRestartPhase.USER_RESTART_REQUIRED, diagnostics);
                machine.SetLastError(diagnostics);
                Persist("live ownership conflict recovered");
                return;
            }

            if (active != null)
            {
                HandleManagedProcessExit(active);
                return;
            }
            string stale = "stale_managed_ownership_recovered;" + ExitDiagnostics();
            ClearLaunchProcessIdentity();
            machine.RecoverStaleOwnership(stale);
            Persist("stale managed ownership recovered");
        }

        private void CaptureExitCode(Process process)
        {
            if (launchRecord == null || process == null) return;
            try
            {
                if (!process.HasExited) return;
                launchRecord.LastExitCode = process.ExitCode;
                launchRecord.LastExitCodeKnown = true;
                launchRecord.LastExitUtc = DateTime.UtcNow;
            }
            catch { launchRecord.LastExitCodeKnown = false; }
        }

        private Process StartOwned()
        {
            if (launchRecord == null || !launchRecord.Owned) throw new InvalidOperationException("owned_launch_required");
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = launchRecord.GamePath,
                Arguments = launchRecord.Arguments ?? string.Empty,
                WorkingDirectory = launchRecord.WorkingDirectory,
                UseShellExecute = false
            };
            ApplyEnvironment(info, launchRecord.Environment);
            Process process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("process_start_failed");
            return process;
        }

        private static void ApplyEnvironment(ProcessStartInfo info, string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized)) return;
            foreach (string item in serialized.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = item.IndexOf('=');
                if (separator <= 0) continue;
                string key = item.Substring(0, separator).Trim();
                if (key.Length == 0 || key.IndexOfAny(new[] { '\r', '\n' }) >= 0) continue;
                info.EnvironmentVariables[key] = item.Substring(separator + 1);
            }
        }

        private static string Limit(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        private bool OwnsProcess(Process process)
        {
            if (launchRecord == null || !launchRecord.Owned || process.Id != launchRecord.ProcessId) return false;
            if (launchRecord.ProcessStartTimeUtcTicks == 0) return false;
            try
            {
                return process.StartTime.ToUniversalTime().Ticks == launchRecord.ProcessStartTimeUtcTicks &&
                    IsExpectedExecutable(process) &&
                    (string.IsNullOrEmpty(launchRecord.ProfileFingerprint) ||
                     string.Equals(launchRecord.ProfileFingerprint, ProfileFingerprint(launchRecord),
                         StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private bool IsExpectedExecutable(Process process)
        {
            if (process == null || launchRecord == null || string.IsNullOrWhiteSpace(launchRecord.GamePath)) return false;
            try
            {
                string actual = process.MainModule.FileName;
                return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(launchRecord.GamePath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ProfileFingerprint(LaunchRecord record)
        {
            string value = string.Join("|", new[]
            {
                record.GamePath ?? string.Empty,
                record.WorkingDirectory ?? string.Empty,
                record.Arguments ?? string.Empty,
                record.ModConfiguration ?? string.Empty,
                record.LaunchProfile ?? string.Empty,
                record.UserDataRoot ?? string.Empty
            });
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
        }

        private bool ReadyForBridge(BridgeRestartCycleRecord cycle, out Dictionary<string, string> status)
        {
            status = ReadStatus();
            if (!string.Equals(Get(status, "bridge"), "ON", StringComparison.OrdinalIgnoreCase)) return false;
            if (launchRecord != null && launchRecord.ProcessId > 0 &&
                Get(status, "processId") != launchRecord.ProcessId.ToString()) return false;
            if (!string.IsNullOrEmpty(cycle.OldBootId) &&
                string.Equals(Get(status, "bootId"), cycle.OldBootId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.NewPid) &&
                !string.Equals(Get(status, "processId"), cycle.NewPid, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredCoreFingerprint) &&
                !CoreFingerprintMatches(cycle.RequiredCoreFingerprint, Get(status, "coreFingerprint"))) return false;
            if (!ReplacementIdentitySatisfied(cycle, status)) return false;
            return true;
        }

        private bool CoreFingerprintMatches(string required, string reported)
        {
            if (string.IsNullOrEmpty(required) || string.IsNullOrEmpty(reported)) return false;
            if (string.Equals(required, reported, StringComparison.OrdinalIgnoreCase)) return true;
            string path = Path.Combine(bridgeRoot, "1.6", "Assemblies", "RimWorldDevBridge.dll");
            if (!File.Exists(path)) return false;
            try
            {
                string fileHash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                    fileHash = string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("X2")));
                string moduleId = Assembly.ReflectionOnlyLoadFrom(path).ManifestModule.ModuleVersionId.ToString("N");
                return (string.Equals(required, fileHash, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reported, moduleId, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(required, moduleId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reported, fileHash, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private static bool ReplacementIdentitySatisfied(BridgeRestartCycleRecord cycle,
            Dictionary<string, string> status)
        {
            if (!cycle.RequiresNewProcess) return true;
            if (!string.IsNullOrEmpty(cycle.OldPid) &&
                string.Equals(Get(status, "processId"), cycle.OldPid, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.OldBootId) &&
                string.Equals(Get(status, "bootId"), cycle.OldBootId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.OldSessionId) &&
                string.Equals(Get(status, "session"), cycle.OldSessionId, StringComparison.OrdinalIgnoreCase)) return false;
            if (cycle.OldLifecycleGeneration > 0 &&
                ParseLong(Get(status, "lifecycleGeneration"), 0) <= cycle.OldLifecycleGeneration) return false;
            return !string.IsNullOrEmpty(Get(status, "session"));
        }

        private bool ReadyForGame(BridgeRestartCycleRecord cycle)
        {
            string package = machine.Snapshot.Tickets.FirstOrDefault(item => item.CycleId == cycle.CycleId)?.PackageId;
            if (!TryBridge("AGENT_CONTEXT", cycle, out string response, "packageId=" + package)) return false;
            if (response.IndexOf("gameLoaded=true", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (cycle.Readiness == "map" && response.IndexOf("mapReady=true", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredAdapterFingerprint) &&
                response.IndexOf(cycle.RequiredAdapterFingerprint, StringComparison.OrdinalIgnoreCase) < 0) return false;
            Dictionary<string, string> status = ReadStatus();
            if (!ReplacementIdentitySatisfied(cycle, status)) return false;
            machine.SetReadyContext(cycle.CycleId, Get(status, "processId"), Get(status, "bootId"),
                Get(status, "session"), Get(status, "transportGeneration"), Get(status, "coreFingerprint"),
                cycle.RequiredAdapterFingerprint, ParseLong(Get(status, "lifecycleGeneration"), 0));
            return true;
        }

        private bool TryBridge(string command, BridgeRestartCycleRecord cycle, out string response,
            string argument = "")
        {
            response = string.Empty;
            try
            {
                Dictionary<string, string> status = ReadStatus();
                string token = Get(status, "token");
                int port;
                if (string.IsNullOrEmpty(token) || !int.TryParse(Get(status, "port"), out port)) return false;
                string id = "restart-" + Guid.NewGuid().ToString("N");
                string session = Get(status, "session");
                string options = "session=" + Uri.EscapeDataString(session) + "&format=line&deadlineMs=3000";
                string line = token + "|" + id + "|" + command + "|" + (argument ?? string.Empty) + "|" + options + "\n";
                using (TcpClient client = new TcpClient("127.0.0.1", port))
                using (NetworkStream stream = client.GetStream())
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    stream.Write(Encoding.UTF8.GetBytes(line), 0, Encoding.UTF8.GetByteCount(line));
                    stream.Flush();
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) response = reader.ReadLine() ?? string.Empty;
                }
                return response.IndexOf("status=OK", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private Dictionary<string, string> ReadStatus()
        {
            string path = Path.Combine(userRoot, "RimWorld-DevBridge-Status.txt");
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator > 0) result[line.Substring(0, separator)] = line.Substring(separator + 1);
            }
            return result;
        }

        private void Load()
        {
            secret = BridgeRestartCoordinatorStateMachine.Secret(secretPath);
            machine = new BridgeRestartCoordinatorStateMachine(BridgeRestartCoordinatorStateMachine.Read(statePath));
            string launchPath = Path.Combine(root, "launch.json");
            if (File.Exists(launchPath))
            {
                using (FileStream stream = File.OpenRead(launchPath))
                    launchRecord = (LaunchRecord)new DataContractJsonSerializer(typeof(LaunchRecord)).ReadObject(stream);
            }
            if (launchRecord != null && string.IsNullOrEmpty(launchRecord.ProfileFingerprint))
                launchRecord.ProfileFingerprint = ProfileFingerprint(launchRecord);
            RecoverStaleOwnedProcess();
        }

        private static bool IsProcessRunning(string processId)
        {
            if (!int.TryParse(processId, out int pid) || pid <= 0) return false;
            try
            {
                using (Process process = Process.GetProcessById(pid)) return !process.HasExited;
            }
            catch { return false; }
        }

        private void Persist(string journal)
        {
            BridgeRestartCoordinatorState snapshot = machine.Snapshot;
            BridgeRestartCoordinatorStateMachine.WriteAtomic(statePath, snapshot);
            File.AppendAllText(journalPath, DateTime.UtcNow.ToString("O") + " " + journal + Environment.NewLine,
                Encoding.UTF8);
            if (launchRecord != null)
            {
                string launchPath = Path.Combine(root, "launch.json");
                string temporary = launchPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                        new DataContractJsonSerializer(typeof(LaunchRecord)).WriteObject(stream, launchRecord);
                    ReplaceAtomic(temporary, launchPath);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
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
            throw last ?? new IOException("atomic launch replacement failed");
        }

        private void AcquireLock()
        {
            lockStream = new FileStream(Path.Combine(root, "coordinator.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }

        private CoordinatorResponse TicketResponse(BridgeRestartTicketRecord ticket, string error = null)
        {
            string terminalError = string.IsNullOrWhiteSpace(error) ? TerminalError(ticket) : error;
            return new CoordinatorResponse
            {
                Ok = ticket != null && string.IsNullOrWhiteSpace(terminalError),
                Error = terminalError,
                Ticket = ticket?.Ticket,
                CycleId = ticket?.CycleId,
                Phase = ticket?.Phase,
                Json = ticket == null ? null : Program.Json(ticket),
                OwnershipJson = Program.Json(GetOwnership()),
                ExitCode = string.IsNullOrWhiteSpace(terminalError) ? 0 : 4
            };
        }

        private static string TerminalError(BridgeRestartTicketRecord ticket)
        {
            if (ticket == null ||
                (ticket.Phase != BridgeRestartPhase.FAILED.ToString() &&
                 ticket.Phase != BridgeRestartPhase.USER_RESTART_REQUIRED.ToString())) return null;
            string value = string.IsNullOrWhiteSpace(ticket.Completion) ? ticket.Reason : ticket.Completion;
            if (string.IsNullOrWhiteSpace(value)) return "managed_launch_failed";
            int separator = value.IndexOf(';');
            return separator > 0 ? value.Substring(0, separator) : value;
        }

        private CoordinatorOwnership GetOwnership()
        {
            CoordinatorOwnership result = new CoordinatorOwnership
            {
                Owned = launchRecord != null && launchRecord.Owned,
                LaunchProfile = launchRecord == null ? string.Empty : launchRecord.LaunchProfile
            };
            if (launchRecord == null || launchRecord.ProcessId <= 0) return result;
            result.ProcessId = launchRecord.ProcessId;
            result.ProcessStartTimeUtcTicks = launchRecord.ProcessStartTimeUtcTicks;
            try
            {
                using (Process process = Process.GetProcessById(launchRecord.ProcessId))
                {
                    result.Running = !process.HasExited && OwnsProcess(process);
                    if (result.Running)
                    {
                        Dictionary<string, string> status = ReadStatus();
                        if (status.ContainsKey("processId") &&
                            status["processId"] == launchRecord.ProcessId.ToString())
                            result.BootId = Get(status, "bootId");
                    }
                }
            }
            catch { result.Running = false; }
            return result;
        }

        private static string PipeName(string value)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return "RimWorldDevBridge-Coordinator-" + BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(value)))).Replace("-", string.Empty).Substring(0, 16);
        }

        private static bool SecureEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            byte[] a = Encoding.UTF8.GetBytes(left);
            byte[] b = Encoding.UTF8.GetBytes(right);
            int result = a.Length ^ b.Length;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                result |= (i < a.Length ? a[i] : (byte)0) ^ (i < b.Length ? b[i] : (byte)0);
            return result == 0;
        }

        private static string ReadBounded(Stream stream)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] chunk = new byte[4096];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaximumMessageBytes) throw new InvalidOperationException("coordinator_message_too_large");
                    if (read < chunk.Length) break;
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string ResponseValue(string response, string key)
        {
            foreach (string line in (response ?? string.Empty).Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string prefix = key + "=";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length);
            }
            return string.Empty;
        }

        private static BridgeRestartPhase ParsePhase(string value)
        {
            BridgeRestartPhase phase;
            return Enum.TryParse(value, true, out phase) ? phase : BridgeRestartPhase.FAILED;
        }

        private static long ParseLong(string value, long fallback = 0L)
        {
            return long.TryParse(value, out long parsed) ? parsed : fallback;
        }
    }
}
