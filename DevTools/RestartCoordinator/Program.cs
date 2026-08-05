using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
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
                LaunchProfile = Get(options, "launch-profile")
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

        internal static string Json(object value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(value.GetType()).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
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
                switch ((message.Operation ?? string.Empty).ToLowerInvariant())
                {
                    case "request": return Request(message);
                    case "status": return Status(message);
                    case "wait": return Wait(message);
                    case "register": return Register(message);
                    case "launch": return Launch(message);
                    case "heartbeat": return Status(message);
                    default: return new CoordinatorResponse { Ok = false, Error = "unknown_coordinator_operation", ExitCode = 2 };
                }
            }
        }

        private CoordinatorResponse Request(CoordinatorMessage message)
        {
            bool owned = launchRecord != null && launchRecord.Owned;
            BridgeRestartTicketRecord ticket = machine.Request(message.AgentId, message.PackageId,
                message.Reason, message.Readiness, message.SavePolicy, message.RequiredCoreFingerprint,
                message.RequiredAdapterFingerprint, owned, message.LiveConfirmedAuthorized,
                message.LiveConfirmed);
            Persist("request " + ticket.Ticket);
            return TicketResponse(ticket);
        }

        private CoordinatorResponse Status(CoordinatorMessage message)
        {
            Pump();
            BridgeRestartTicketRecord ticket = string.IsNullOrEmpty(message.Ticket) ? null : machine.Ticket(message.Ticket);
            return new CoordinatorResponse
            {
                Ok = ticket != null,
                Error = ticket == null ? "unknown_restart_ticket" : null,
                Ticket = ticket?.Ticket,
                CycleId = ticket?.CycleId,
                Phase = ticket?.Phase,
                Json = Program.Json(machine.Snapshot),
                ExitCode = ticket == null ? 2 : 0
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
            return TicketResponse(timeout, "coordinator_wait_timeout");
        }

        private CoordinatorResponse Register(CoordinatorMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.GamePath) || !File.Exists(message.GamePath))
                return new CoordinatorResponse { Ok = false, Error = "validated_game_executable_required", ExitCode = 2 };
            string working = string.IsNullOrWhiteSpace(message.WorkingDirectory) ?
                Path.GetDirectoryName(Path.GetFullPath(message.GamePath)) : Path.GetFullPath(message.WorkingDirectory);
            if (!Directory.Exists(working)) return new CoordinatorResponse { Ok = false, Error = "working_directory_missing", ExitCode = 2 };
            launchRecord = new LaunchRecord
            {
                GamePath = Path.GetFullPath(message.GamePath),
                WorkingDirectory = working,
                Arguments = message.Arguments ?? string.Empty,
                SavePath = message.SavePath,
                Owned = message.Owned,
                ModConfiguration = Limit(message.ModConfiguration, 2048),
                Environment = Limit(message.Environment, 8192),
                LaunchProfile = Limit(message.LaunchProfile, 256)
            };
            Persist("register launch");
            return new CoordinatorResponse { Ok = true, Json = Program.Json(launchRecord) };
        }

        private CoordinatorResponse Launch(CoordinatorMessage message)
        {
            if (!message.Owned)
                return new CoordinatorResponse { Ok = false, Error = "attached_process_cannot_be_launched", ExitCode = 4 };
            CoordinatorResponse registered = Register(message);
            if (!registered.Ok) return registered;
            Process process = StartOwned();
            launchRecord.ProcessId = process.Id;
            launchRecord.ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            Persist("launch pid=" + process.Id);
            return new CoordinatorResponse { Ok = true, Json = Program.Json(launchRecord), ExitCode = 0 };
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
                    if (string.IsNullOrEmpty(cycle.OldPid) && string.IsNullOrEmpty(cycle.OldBootId))
                    {
                        Dictionary<string, string> beforeDrain = ReadStatus();
                        machine.SetCycleIdentity(cycle.CycleId, Get(beforeDrain, "processId"),
                            Get(beforeDrain, "bootId"));
                        cycle = machine.Cycle(cycle.CycleId);
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
                            "attached_process_user_restart_required");
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
                    if (ReadyForBridge(cycle))
                    {
                        machine.SetPhase(cycle.CycleId, BridgeRestartPhase.WAITING_FOR_GAME);
                        Persist("bridge ready cycle=" + cycle.CycleId);
                    }
                    break;
                case BridgeRestartPhase.WAITING_FOR_GAME:
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
                Process process = launchRecord == null || launchRecord.ProcessId == 0 ? null :
                    Process.GetProcessById(launchRecord.ProcessId);
                if (process != null && !process.HasExited)
                {
                    if (!OwnsProcess(process))
                    {
                        machine.Fail(cycle.CycleId, "owned_process_identity_mismatch");
                        Persist("stop identity mismatch");
                        return;
                    }
                    process.CloseMainWindow();
                    if (!process.WaitForExit(15000) && forceKillTestOnly) process.Kill();
                    if (!process.HasExited)
                    {
                        machine.Fail(cycle.CycleId, "owned_process_did_not_exit_without_force_kill");
                        Persist("stop failed");
                        return;
                    }
                }
                machine.SetPhase(cycle.CycleId, BridgeRestartPhase.STARTING);
                Persist("stopped cycle=" + cycle.CycleId);
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
            try
            {
                Process process = StartOwned();
                launchRecord.ProcessId = process.Id;
                launchRecord.ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                machine.SetStartedPid(cycle.CycleId, process.Id.ToString());
                machine.SetPhase(cycle.CycleId, BridgeRestartPhase.WAITING_FOR_BRIDGE);
                Persist("started pid=" + process.Id);
            }
            catch (Exception exception)
            {
                machine.Fail(cycle.CycleId, "start_failed:" + exception.GetType().Name);
                Persist("start exception");
            }
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
                return process.StartTime.ToUniversalTime().Ticks == launchRecord.ProcessStartTimeUtcTicks;
            }
            catch { return false; }
        }

        private bool ReadyForBridge(BridgeRestartCycleRecord cycle)
        {
            Dictionary<string, string> status = ReadStatus();
            if (!string.Equals(Get(status, "bridge"), "ON", StringComparison.OrdinalIgnoreCase)) return false;
            if (launchRecord != null && launchRecord.ProcessId > 0 &&
                Get(status, "processId") != launchRecord.ProcessId.ToString()) return false;
            if (!string.IsNullOrEmpty(cycle.OldBootId) &&
                string.Equals(Get(status, "bootId"), cycle.OldBootId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.NewPid) &&
                !string.Equals(Get(status, "processId"), cycle.NewPid, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredCoreFingerprint) &&
                !string.Equals(Get(status, "coreFingerprint"), cycle.RequiredCoreFingerprint,
                    StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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
            machine.SetReadyContext(cycle.CycleId, Get(status, "processId"), Get(status, "bootId"),
                Get(status, "session"), Get(status, "transportGeneration"), Get(status, "coreFingerprint"),
                cycle.RequiredAdapterFingerprint);
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
                    if (File.Exists(launchPath)) File.Replace(temporary, launchPath, null);
                    else File.Move(temporary, launchPath);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        private void AcquireLock()
        {
            lockStream = new FileStream(Path.Combine(root, "coordinator.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }

        private CoordinatorResponse TicketResponse(BridgeRestartTicketRecord ticket, string error = null)
        {
            return new CoordinatorResponse
            {
                Ok = ticket != null && error == null,
                Error = error,
                Ticket = ticket?.Ticket,
                CycleId = ticket?.CycleId,
                Phase = ticket?.Phase,
                Json = ticket == null ? null : Program.Json(ticket),
                ExitCode = error == null ? 0 : 4
            };
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
    }
}
