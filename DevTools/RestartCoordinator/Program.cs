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
        [DataMember(Order = 32)] public string ClientInstanceId;
        [DataMember(Order = 33)] public string ConnectionSessionId;
        [DataMember(Order = 34)] public string CorrelationId;
        [DataMember(Order = 35)] public string ParticipantId;
        [DataMember(Order = 36)] public string CompatibilityKey;
        [DataMember(Order = 37)] public string OperationId;
        [DataMember(Order = 38)] public string RuntimeSlotId;
        [DataMember(Order = 39)] public string DeploymentId;
        [DataMember(Order = 40)] public string ArtifactFingerprint;
        [DataMember(Order = 41)] public string LoadedAssemblyFingerprint;
        [DataMember(Order = 42)] public string ManagedProfile;
        [DataMember(Order = 43)] public string RimWorldVersion;
        [DataMember(Order = 44)] public string ModSetFingerprint;
        [DataMember(Order = 45)] public string ModLoadOrderFingerprint;
        [DataMember(Order = 46)] public string SourceBuildIdentity;
        [DataMember(Order = 47)] public string ConfigurationFingerprint;
        [DataMember(Order = 48)] public string UserRootFingerprint;
        [DataMember(Order = 49)] public string SaveTarget;
        [DataMember(Order = 50)] public string MapTarget;
        [DataMember(Order = 51)] public string MutationScope;
        [DataMember(Order = 52)] public string ClientCredential;
        [DataMember(Order = 53)] public string SandboxAuthorizationPath;
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
        [DataMember(Order = 17)] public string SandboxAuthorizationPath;
    }

    [DataContract]
    internal sealed class SandboxAuthorizationRecord
    {
        [DataMember] public int schema { get; set; }
        [DataMember] public string policy { get; set; }
        [DataMember] public string scope { get; set; }
        [DataMember] public bool operatorConfirmed { get; set; }
        [DataMember] public string profile { get; set; }
        [DataMember] public string executable { get; set; }
        [DataMember] public string executableSha256 { get; set; }
        [DataMember] public string workingDirectory { get; set; }
        [DataMember] public string arguments { get; set; }
        [DataMember] public string userDataRoot { get; set; }
        [DataMember] public string modConfiguration { get; set; }
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
                AllowSupersede = options.ContainsKey("allow-supersede"),
                ClientInstanceId = Get(options, "client-instance-id"),
                ConnectionSessionId = Get(options, "connection-session-id"),
                CorrelationId = Get(options, "correlation-id"),
                ParticipantId = Get(options, "participant-id"),
                CompatibilityKey = Get(options, "compatibility-key"),
                OperationId = Get(options, "operation-id"),
                RuntimeSlotId = Get(options, "runtime-slot-id"),
                 DeploymentId = Get(options, "deployment-id"),
                 ArtifactFingerprint = Get(options, "artifact-fingerprint"),
                 LoadedAssemblyFingerprint = Get(options, "loaded-assembly-fingerprint"),
                 ManagedProfile = Get(options, "managed-profile", Get(options, "launch-profile")),
                 RimWorldVersion = Get(options, "rimworld-version"),
                 ModSetFingerprint = Get(options, "mod-set-fingerprint"),
                 ModLoadOrderFingerprint = Get(options, "mod-load-order-fingerprint"),
                 SourceBuildIdentity = Get(options, "source-build-identity"),
                 ConfigurationFingerprint = Get(options, "configuration-fingerprint"),
                 UserRootFingerprint = Get(options, "user-root-fingerprint"),
                 SaveTarget = Get(options, "save-target"),
                 MapTarget = Get(options, "map-target"),
                  MutationScope = Get(options, "mutation-scope"),
                  ClientCredential = Get(options, "client-credential"),
                  SandboxAuthorizationPath = Get(options, "sandbox-authorization-path")
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
        private readonly string credentialRoot;
        private readonly string pipeName;
        private readonly bool forceKillTestOnly;
        private readonly bool acquireWriterLock;
        private readonly object gate = new object();
        private readonly BridgeRuntimeSlotManager runtimeSlots;
        private BridgeRestartCoordinatorStateMachine machine;
        private FileStream lockStream;
        private string secret;
        private LaunchRecord launchRecord;
        private Process monitoredProcess;
        private readonly Dictionary<string, string> cycleCredentials =
            new Dictionary<string, string>(StringComparer.Ordinal);

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
            credentialRoot = Path.Combine(this.root, "credentials");
            pipeName = PipeName(this.root);
            runtimeSlots = new BridgeRuntimeSlotManager(2,
                root: Path.Combine(this.userRoot, "Coordination", "slots"));
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
                RememberCredential(message, message.Ticket);
                RecoverStaleOwnedProcess();
                switch ((message.Operation ?? string.Empty).ToLowerInvariant())
                {
                    case "request": return Request(message);
                    case "status": return Status(message);
                    case "wait": return Wait(message);
                    case "register": return Register(message);
                    case "launch": return Launch(message);
                    case "ensure": return Ensure(message);
                    case "heartbeat": return Heartbeat(message);
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
            if (string.IsNullOrWhiteSpace(message.RuntimeSlotId))
            {
                string rootValue = root;
                message.RuntimeSlotId = "slot-" + BridgeHashing.Sha256(rootValue).Substring(0, 16);
            }
            bool owned = launchRecord != null && launchRecord.Owned;
            BridgeClientIdentity identity = IdentityFor(message);
            string agentId = identity.AgentId;
            BridgeOperationCompatibilityKey compatibility = LegacyCompatibility(message, requiresNewProcess);
            if (!string.IsNullOrWhiteSpace(message.CompatibilityKey) &&
                !string.Equals(compatibility.ToString(),
                    BridgeOperationCompatibilityKey.FromDigest(message.CompatibilityKey).ToString(),
                    StringComparison.Ordinal))
                return new CoordinatorResponse { Ok = false, Error = "compatibility_key_mismatch", ExitCode = 2 };
            BridgeRestartTicketRecord ticket = machine.Request(agentId, message.PackageId,
                message.Reason, message.Readiness, message.SavePolicy, message.RequiredCoreFingerprint,
                message.RequiredAdapterFingerprint, owned, message.LiveConfirmedAuthorized,
                message.LiveConfirmed, processAlreadyStarted, message.MaxLaunchAttempts,
                message.LaunchBackoffMs, message.TargetPostcondition, requiresNewProcess,
                message.RequestedLifecycleGeneration, message.RequestedPid, message.RequestedSessionId,
                message.AllowSupersede, message.TimeoutMs, identity, compatibility, message.OperationId,
                message.RuntimeSlotId, message.DeploymentId, message.ArtifactFingerprint,
                 message.LoadedAssemblyFingerprint);
             RememberCredential(message, ticket.Ticket);
            Persist("request " + ticket.Ticket);
            return TicketResponse(ticket);
        }

        private CoordinatorResponse Status(CoordinatorMessage message)
        {
            Pump();
            BridgeClientIdentity identity = IdentityFor(message);
            BridgeRestartTicketRecord ticket = string.IsNullOrEmpty(message.Ticket) ? null : machine.Ticket(message.Ticket, identity);
            string terminalError = TerminalError(ticket);
            return new CoordinatorResponse
            {
                Ok = ticket != null && string.IsNullOrWhiteSpace(terminalError),
                Error = ticket == null ? "unknown_restart_ticket" : terminalError,
                Ticket = ticket?.Ticket,
                CycleId = ticket?.CycleId,
                Phase = ticket?.Phase,
                Json = Program.Json(SnapshotFor(identity)),
                OwnershipJson = Program.Json(GetOwnership()),
                ExitCode = ticket == null ? 2 : 0
            };
        }

        private CoordinatorResponse Heartbeat(CoordinatorMessage message)
        {
            Pump();
            BridgeClientIdentity identity = IdentityFor(message);
            return new CoordinatorResponse
            {
                Ok = true,
                Phase = machine.Snapshot.Phase.ToString(),
                Json = Program.Json(SnapshotFor(identity)),
                OwnershipJson = Program.Json(GetOwnership()),
                ExitCode = 0
            };
        }

        private CoordinatorResponse Wait(CoordinatorMessage message)
        {
            BridgeClientIdentity identity = IdentityFor(message);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, message.TimeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                Pump();
                BridgeRestartTicketRecord ticket = machine.Ticket(message.Ticket, identity);
                if (ticket == null) return new CoordinatorResponse { Ok = false, Error = "unknown_restart_ticket", ExitCode = 2 };
                BridgeRestartPhase phase = (BridgeRestartPhase)Enum.Parse(typeof(BridgeRestartPhase), ticket.Phase);
                if (phase == BridgeRestartPhase.READY || phase == BridgeRestartPhase.FAILED ||
                    phase == BridgeRestartPhase.USER_RESTART_REQUIRED)
                    return TicketResponse(ticket);
                Thread.Sleep(250);
            }
            BridgeRestartTicketRecord timeout = machine.Ticket(message.Ticket, identity);
            string timeoutError = timeout != null &&
                (timeout.Phase == BridgeRestartPhase.STARTING.ToString() ||
                 timeout.Phase == BridgeRestartPhase.WAITING_FOR_BRIDGE.ToString()) ?
                "bridge_handshake_timeout" : "coordinator_wait_timeout";
            return TicketResponse(timeout, timeoutError);
        }

        private CoordinatorResponse Ensure(CoordinatorMessage message)
        {
            if (launchRecord != null && launchRecord.Owned && !LaunchRecordAuthorized())
            {
                launchRecord.Owned = false;
                Persist("managed launch authorization no longer valid");
            }
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
            if (message.Owned && !string.Equals(message.LaunchProfile, "managed-test",
                    StringComparison.OrdinalIgnoreCase))
                return new CoordinatorResponse { Ok = false, Error = "validated_launch_profile_required", ExitCode = 2 };
            string working = string.IsNullOrWhiteSpace(message.WorkingDirectory) ?
                Path.GetDirectoryName(Path.GetFullPath(message.GamePath)) : Path.GetFullPath(message.WorkingDirectory);
            if (!Directory.Exists(working)) return new CoordinatorResponse { Ok = false, Error = "working_directory_missing", ExitCode = 2 };
            string userDataRoot = string.IsNullOrWhiteSpace(message.UserDataRoot) ? userRoot :
                Path.GetFullPath(message.UserDataRoot);
            if (!Directory.Exists(userDataRoot)) return new CoordinatorResponse { Ok = false, Error = "user_data_root_missing", ExitCode = 2 };
            if (!StringComparer.OrdinalIgnoreCase.Equals(userDataRoot, userRoot))
                return new CoordinatorResponse { Ok = false, Error = "managed_test_user_root_mismatch", ExitCode = 2 };
            if (message.Owned && !SandboxAuthorized(message, message.GamePath, working, userDataRoot,
                    message.Arguments ?? string.Empty, message.ModConfiguration ?? string.Empty))
                return new CoordinatorResponse { Ok = false, Error = "sandbox_authorization_required", ExitCode = 3 };
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
                SandboxAuthorizationPath = message.SandboxAuthorizationPath,
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

        private bool SandboxAuthorized(CoordinatorMessage message, string gamePath, string workingDirectory,
            string userDataRoot, string arguments, string modConfiguration)
        {
            string path = string.IsNullOrWhiteSpace(message.SandboxAuthorizationPath) ?
                Path.Combine(userRoot, "RimWorld-DevBridge-SandboxAuthorization.json") :
                Path.GetFullPath(message.SandboxAuthorizationPath);
            string rootPrefix = userRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            try
            {
                SandboxAuthorizationRecord authorization;
                byte[] authorizationBytes = File.ReadAllBytes(path);
                int authorizationOffset = authorizationBytes.Length >= 3 && authorizationBytes[0] == 0xEF &&
                    authorizationBytes[1] == 0xBB && authorizationBytes[2] == 0xBF ? 3 : 0;
                using (MemoryStream stream = new MemoryStream(authorizationBytes, authorizationOffset,
                    authorizationBytes.Length - authorizationOffset, false))
                    authorization = (SandboxAuthorizationRecord)new DataContractJsonSerializer(
                        typeof(SandboxAuthorizationRecord)).ReadObject(stream);
                string executable = Path.GetFullPath(gamePath);
                string expectedHash = BridgeHashing.FileSha256(executable);
                return authorization != null && authorization.schema == 1 &&
                    authorization.policy == "explicit-operator-disposable-sandbox" &&
                    authorization.scope == "coordinator-owned-managed-test" && authorization.operatorConfirmed &&
                    authorization.profile == "managed-test" &&
                    StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(authorization.userDataRoot), userRoot) &&
                    StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(authorization.executable), executable) &&
                    StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(authorization.workingDirectory),
                        Path.GetFullPath(workingDirectory)) && authorization.arguments == arguments &&
                    authorization.modConfiguration == modConfiguration &&
                    StringComparer.OrdinalIgnoreCase.Equals(authorization.executableSha256, expectedHash);
            }
            catch { return false; }
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
                            ParseLong(Get(bridgeStatus, "lifecycleGeneration"), 0),
                            Get(bridgeStatus, "loadedAssemblyFingerprint"));
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
                if (!RecordCoordinatorLaunch(cycle, startedProcess))
                    throw new InvalidOperationException("managed_launch_provenance_required");
                machine.SetPhase(cycle.CycleId, BridgeRestartPhase.WAITING_FOR_BRIDGE);
                Persist("started pid=" + startedProcess.Id);
            }
            catch (Exception exception)
            {
                CloseStartedProcess(startedProcess);
                HandleManagedLaunchFailure(cycle, "launch_profile_invalid:" + exception.GetType().Name);
            }
        }

        private bool RecordCoordinatorLaunch(BridgeRestartCycleRecord cycle, Process process)
        {
            if (cycle == null || process == null || string.IsNullOrWhiteSpace(cycle.RuntimeSlotId) ||
                string.IsNullOrWhiteSpace(cycle.OperationId)) return false;
            BridgeOperationCompatibilityKey compatibility = CycleCompatibility(cycle);
            string start = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            BridgeProcessIdentity expected = new BridgeProcessIdentity
            {
                Pid = process.Id,
                ProcessStartIdentity = start,
                ExecutablePath = launchRecord.GamePath,
                ProfileFingerprint = BridgeHashing.Sha256(cycle.ManagedProfile ?? string.Empty),
                RuntimeSlotId = cycle.RuntimeSlotId,
                LifecycleGeneration = compatibility.LifecycleGeneration,
                LoadedAssemblyFingerprint = cycle.LoadedAssemblyFingerprint ?? string.Empty
            };
            BridgeRuntimeSlotRequest slotRequest = new BridgeRuntimeSlotRequest
            {
                Compatibility = compatibility,
                RequestedRuntimeSlotId = cycle.RuntimeSlotId,
                AgentId = cycle.PrimaryAgentId,
                ClientInstanceId = cycle.PrimaryClientInstanceId,
                OperationId = cycle.OperationId,
                RequiresNewProcess = cycle.RequiresNewProcess
            };
            bool recorded = runtimeSlots.RecordCoordinatorLaunch(slotRequest, expected);
            return recorded;
        }

        private static BridgeOperationCompatibilityKey CycleCompatibility(BridgeRestartCycleRecord cycle)
        {
            BridgeDesiredState desired;
            if (!TryParseDesiredState(cycle.TargetPostcondition ?? cycle.Readiness ?? "bridge", out desired))
                desired = BridgeDesiredState.Bridge;
            return BridgeOperationCompatibilityKey.Create(BridgeOperationKind.Restart, desired,
                cycle.ManagedProfile ?? "managed-test", cycle.RimWorldVersion ?? "unknown-rimworld-version",
                cycle.ModSetFingerprint ?? "unknown-mod-set", cycle.ModLoadOrderFingerprint ?? "unknown-load-order",
                cycle.SourceBuildIdentity ?? cycle.RequiredCoreFingerprint ?? "unknown-build",
                cycle.RuntimeSlotId ?? "default", cycle.RequiredCoreFingerprint ?? "unknown-core",
                cycle.RequiredAdapterFingerprint ?? "unknown-adapter", cycle.ConfigurationFingerprint ?? "unknown-config",
                cycle.UserRootFingerprint ?? "unknown-user-root", cycle.SaveTarget ?? cycle.SavePolicy ?? "none",
                cycle.MapTarget ?? "unknown-map", cycle.RequiresNewProcess, cycle.RequestedLifecycleGeneration,
                cycle.MutationScope ?? "restart", cycle.DeploymentId, cycle.ArtifactFingerprint,
                cycle.LoadedAssemblyFingerprint);
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
            ClearCoordinatorProvenance();
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

        private void ClearCoordinatorProvenance()
        {
            if (launchRecord == null || launchRecord.ProcessId <= 0) return;
            BridgeRestartCycleRecord cycle = machine.Snapshot.Cycles.FirstOrDefault(item =>
                item.OperationId != null && item.RuntimeSlotId != null &&
                string.Equals(item.NewPid, launchRecord.ProcessId.ToString(), StringComparison.Ordinal));
            if (cycle == null) return;
            BridgeProcessIdentity expected = new BridgeProcessIdentity
            {
                Pid = launchRecord.ProcessId,
                ProcessStartIdentity = launchRecord.ProcessStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                ExecutablePath = launchRecord.GamePath,
                ProfileFingerprint = BridgeHashing.Sha256(cycle.ManagedProfile ?? string.Empty),
                RuntimeSlotId = cycle.RuntimeSlotId,
                LifecycleGeneration = cycle.RequestedLifecycleGeneration,
                LoadedAssemblyFingerprint = cycle.LoadedAssemblyFingerprint ?? string.Empty
            };
            runtimeSlots.ClearCoordinatorLaunch(cycle.RuntimeSlotId, cycle.OperationId,
                cycle.PrimaryAgentId, cycle.PrimaryClientInstanceId, expected);
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
            if (!LaunchRecordAuthorized()) throw new InvalidOperationException("sandbox_authorization_required");
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

        private bool LaunchRecordAuthorized()
        {
            if (launchRecord == null) return false;
            return SandboxAuthorized(new CoordinatorMessage
            {
                GamePath = launchRecord.GamePath,
                WorkingDirectory = launchRecord.WorkingDirectory,
                Arguments = launchRecord.Arguments,
                UserDataRoot = launchRecord.UserDataRoot,
                ModConfiguration = launchRecord.ModConfiguration,
                SandboxAuthorizationPath = launchRecord.SandboxAuthorizationPath,
                LaunchProfile = launchRecord.LaunchProfile,
                Owned = true
            }, launchRecord.GamePath, launchRecord.WorkingDirectory, launchRecord.UserDataRoot,
                launchRecord.Arguments ?? string.Empty, launchRecord.ModConfiguration ?? string.Empty);
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
            if (!string.IsNullOrEmpty(cycle.RequiredAdapterFingerprint) &&
                !string.Equals(cycle.RequiredAdapterFingerprint, Get(status, "adapterFingerprint"),
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.ArtifactFingerprint) &&
                !string.Equals(cycle.ArtifactFingerprint, Get(status, "artifactFingerprint"),
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cycle.LoadedAssemblyFingerprint) &&
                !string.Equals(cycle.LoadedAssemblyFingerprint, Get(status, "loadedAssemblyFingerprint"),
                    StringComparison.OrdinalIgnoreCase)) return false;
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
            if ((cycle.Readiness == "map" || cycle.TargetPostcondition == "map" ||
                cycle.TargetPostcondition == "test_ready") &&
                response.IndexOf("mapReady=true", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrEmpty(cycle.RequiredAdapterFingerprint) &&
                response.IndexOf(cycle.RequiredAdapterFingerprint, StringComparison.OrdinalIgnoreCase) < 0) return false;
            Dictionary<string, string> status = ReadStatus();
            if (!ReplacementIdentitySatisfied(cycle, status)) return false;
            machine.SetReadyContext(cycle.CycleId, Get(status, "processId"), Get(status, "bootId"),
                Get(status, "session"), Get(status, "transportGeneration"), Get(status, "coreFingerprint"),
                cycle.RequiredAdapterFingerprint, ParseLong(Get(status, "lifecycleGeneration"), 0),
                Get(status, "loadedAssemblyFingerprint"), Get(status, "processStartIdentity"));
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
                BridgeRestartTicketRecord ticket = machine.Snapshot.Tickets.FirstOrDefault(item =>
                    string.Equals(item.CycleId, cycle.CycleId, StringComparison.Ordinal));
                List<string> optionValues = new List<string>
                {
                    "session=" + Uri.EscapeDataString(session ?? string.Empty),
                    "format=line",
                    "deadlineMs=3000",
                    "agentId=" + Uri.EscapeDataString(ticket?.AgentId ?? cycle.PrimaryAgentId ?? "restart-coordinator"),
                    "clientInstanceId=" + Uri.EscapeDataString(ticket?.ClientInstanceId ?? cycle.PrimaryClientInstanceId ?? "restart-client"),
                    "participantId=" + Uri.EscapeDataString(ticket?.ParticipantId ?? "restart-participant"),
                    "correlationId=" + Uri.EscapeDataString(ticket?.CorrelationId ?? id),
                    "operationKind=Restart",
                    "desiredState=" + Uri.EscapeDataString(cycle.TargetPostcondition ?? "bridge")
                };
                string credential;
                if (ticket != null && TryGetCredential(ticket.Ticket, out credential) &&
                    !string.IsNullOrWhiteSpace(credential))
                    optionValues.Add("clientCredential=" + Uri.EscapeDataString(credential));
                if (!string.IsNullOrEmpty(cycle.OperationId))
                    optionValues.Add("operationId=" + Uri.EscapeDataString(cycle.OperationId));
                if (!string.IsNullOrEmpty(cycle.CompatibilityKey))
                    optionValues.Add("compatibilityKey=" + Uri.EscapeDataString(cycle.CompatibilityKey));
                if (!string.IsNullOrEmpty(cycle.RuntimeSlotId))
                    optionValues.Add("runtimeSlotId=" + Uri.EscapeDataString(cycle.RuntimeSlotId));
                if (!string.IsNullOrEmpty(cycle.DeploymentId))
                    optionValues.Add("deploymentId=" + Uri.EscapeDataString(cycle.DeploymentId));
                if (!string.IsNullOrEmpty(cycle.ArtifactFingerprint))
                    optionValues.Add("artifactFingerprint=" + Uri.EscapeDataString(cycle.ArtifactFingerprint));
                if (!string.IsNullOrEmpty(cycle.LoadedAssemblyFingerprint))
                    optionValues.Add("loadedAssemblyFingerprint=" + Uri.EscapeDataString(cycle.LoadedAssemblyFingerprint));
                AddBridgeOption(optionValues, "expectedProcessId", cycle.NewPid);
                AddBridgeOption(optionValues, "expectedProcessStartIdentity", ticket?.ProcessStartIdentity ??
                    cycle.ProcessStartIdentity);
                AddBridgeOption(optionValues, "expectedProcessSessionId", cycle.NewSessionId);
                if (cycle.NewLifecycleGeneration > 0)
                    AddBridgeOption(optionValues, "expectedProcessLifecycleGeneration",
                        cycle.NewLifecycleGeneration.ToString(CultureInfo.InvariantCulture));
                AddBridgeOption(optionValues, "managedProfile", cycle.ManagedProfile);
                AddBridgeOption(optionValues, "rimWorldVersion", cycle.RimWorldVersion);
                AddBridgeOption(optionValues, "modSetFingerprint", cycle.ModSetFingerprint);
                AddBridgeOption(optionValues, "modLoadOrderFingerprint", cycle.ModLoadOrderFingerprint);
                AddBridgeOption(optionValues, "sourceBuildIdentity", cycle.SourceBuildIdentity);
                AddBridgeOption(optionValues, "configurationFingerprint", cycle.ConfigurationFingerprint);
                AddBridgeOption(optionValues, "userRootFingerprint", cycle.UserRootFingerprint);
                AddBridgeOption(optionValues, "saveTarget", cycle.SaveTarget);
                AddBridgeOption(optionValues, "mapTarget", cycle.MapTarget);
                AddBridgeOption(optionValues, "mutationScope", cycle.MutationScope);
                if (cycle.NewLifecycleGeneration > 0)
                {
                    AddBridgeOption(optionValues, "lifecycleGeneration",
                        cycle.NewLifecycleGeneration.ToString(CultureInfo.InvariantCulture));
                }
                if (cycle.RequiresNewProcess)
                    optionValues.Add("requiresProcessReplacement=true");
                string options = string.Join("&", optionValues.ToArray());
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

        private void RememberCredential(CoordinatorMessage message, string ticketId)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.ClientCredential) ||
                string.IsNullOrWhiteSpace(ticketId)) return;
            cycleCredentials[ticketId] = message.ClientCredential;
            try
            {
                Directory.CreateDirectory(credentialRoot);
                byte[] protectedValue = ProtectedData.Protect(Encoding.UTF8.GetBytes(message.ClientCredential),
                    null, DataProtectionScope.CurrentUser);
                string path = Path.Combine(credentialRoot, BridgeHashing.Sha256(ticketId) + ".bin");
                string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, protectedValue);
                ReplaceAtomic(temporary, path);
            }
            catch { }
        }

        private bool TryGetCredential(string ticketId, out string credential)
        {
            credential = null;
            if (string.IsNullOrWhiteSpace(ticketId)) return false;
            if (cycleCredentials.TryGetValue(ticketId, out credential) &&
                !string.IsNullOrWhiteSpace(credential)) return true;
            try
            {
                string path = Path.Combine(credentialRoot, BridgeHashing.Sha256(ticketId) + ".bin");
                if (!File.Exists(path)) return false;
                credential = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path),
                    null, DataProtectionScope.CurrentUser));
                if (string.IsNullOrWhiteSpace(credential)) return false;
                cycleCredentials[ticketId] = credential;
                return true;
            }
            catch
            {
                credential = null;
                return false;
            }
        }

        private static void AddBridgeOption(List<string> options, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                options.Add(name + "=" + Uri.EscapeDataString(value));
        }

        private static BridgeClientIdentity IdentityFor(CoordinatorMessage message)
        {
            string agent = string.IsNullOrWhiteSpace(message?.AgentId) ? "coordinator-client" : message.AgentId;
            string client = string.IsNullOrWhiteSpace(message?.ClientInstanceId) ? "client-coordinator" :
                message.ClientInstanceId;
            string connection = string.IsNullOrWhiteSpace(message?.ConnectionSessionId) ?
                "connection-coordinator" : message.ConnectionSessionId;
            string correlation = string.IsNullOrWhiteSpace(message?.CorrelationId) ?
                "correlation-coordinator" : message.CorrelationId;
            string participant = string.IsNullOrWhiteSpace(message?.ParticipantId) ?
                "participant-" + BridgeHashing.Sha256(agent).Substring(0, 16).ToLowerInvariant() :
                message.ParticipantId;
            return BridgeClientIdentity.Create(agent, client, connection, correlation, participant);
        }

        private static bool TryParseDesiredState(string value, out BridgeDesiredState desired)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "bridge" : value.Trim();
            normalized = normalized.Replace("_", string.Empty).Replace("-", string.Empty);
            if (string.Equals(normalized, "testready", StringComparison.OrdinalIgnoreCase))
            {
                desired = BridgeDesiredState.TestReady;
                return true;
            }
            return Enum.TryParse(normalized, true, out desired);
        }

        private static BridgeOperationCompatibilityKey LegacyCompatibility(CoordinatorMessage message,
            bool? effectiveRequiresNewProcess = null)
        {
            BridgeDesiredState desired;
            if (!TryParseDesiredState(message.TargetPostcondition ?? message.Readiness ?? "bridge", out desired))
                desired = BridgeDesiredState.Bridge;
            string userRoot = string.IsNullOrWhiteSpace(message.UserDataRoot) ? "unknown-user-root" :
                BridgeHashing.Sha256(Path.GetFullPath(message.UserDataRoot));
            return BridgeOperationCompatibilityKey.Create(BridgeOperationKind.Restart, desired,
                message.ManagedProfile ?? message.LaunchProfile ?? "managed-test",
                message.RimWorldVersion ?? "unknown-rimworld-version",
                message.ModSetFingerprint ?? "unknown-mod-set",
                message.ModLoadOrderFingerprint ?? message.ModConfiguration ?? "unknown-load-order",
                message.SourceBuildIdentity ?? message.RequiredCoreFingerprint ?? "unknown-build",
                message.RuntimeSlotId ?? "default", message.RequiredCoreFingerprint ?? "unknown-core",
                message.RequiredAdapterFingerprint ?? "unknown-adapter",
                message.ConfigurationFingerprint ?? BridgeHashing.Sha256(
                    (message.ModConfiguration ?? "unknown-config") + "|" + (message.Arguments ?? string.Empty)),
                message.UserRootFingerprint ?? userRoot,
                message.SaveTarget ?? message.SavePath ?? message.SavePolicy ?? "none",
                message.MapTarget ?? "unknown-map", effectiveRequiresNewProcess ?? message.RequiresNewProcess,
                message.RequestedLifecycleGeneration, message.MutationScope ?? "restart",
                message.DeploymentId, message.ArtifactFingerprint, message.LoadedAssemblyFingerprint);
        }

        private BridgeRestartCoordinatorState SnapshotFor(BridgeClientIdentity identity)
        {
            BridgeRestartCoordinatorState result = Program.FromJson<BridgeRestartCoordinatorState>(
                Program.Json(machine.Snapshot));
            result.Tickets = result.Tickets.Where(ticket => ticket.AgentId == identity.AgentId &&
                ticket.ClientInstanceId == identity.ClientInstanceId &&
                ticket.ParticipantId == identity.ParticipantId).ToList();
            HashSet<string> cycles = new HashSet<string>(result.Tickets.Select(ticket => ticket.CycleId),
                StringComparer.Ordinal);
            result.Cycles = result.Cycles.Where(cycle => cycles.Contains(cycle.CycleId)).ToList();
            foreach (BridgeRestartCycleRecord cycle in result.Cycles)
            {
                cycle.TicketIds = cycle.TicketIds.Where(ticketId => result.Tickets.Any(ticket =>
                    ticket.Ticket == ticketId)).ToList();
                cycle.ParticipantIds = cycle.ParticipantIds.Where(participant =>
                    participant == identity.ParticipantId).ToList();
            }
            return result;
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
