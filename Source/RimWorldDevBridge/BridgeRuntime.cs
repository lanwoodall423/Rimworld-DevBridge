using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeRuntime
    {
        private const int IdleSeconds = 180;
        private static readonly object Gate = new object();
        private static readonly BridgeAuthorization Authorization = new BridgeAuthorization();
        private static readonly BridgeScheduler Scheduler = new BridgeScheduler(ExecuteScheduled);
        private static readonly string BootId = Guid.NewGuid().ToString("N");
        private static readonly BridgeMainThreadContext MainThread = new BridgeMainThreadContext();
        private static FileSystemWatcher watcher;
        private static TcpListener listener;
        private static Thread listenerThread;
        private static Timer idleTimer;
        private static volatile bool active;
        private static volatile bool shuttingDown;
        private static int transportGeneration;
        private static int activeClients;
        private static int port;
        private static string token = string.Empty;
        private static string sessionId = "menu-" + Guid.NewGuid().ToString("N");
        private static long lastActivityUtcTicks;
        private static long bootstrapTicks;
        private static double harmonyMs;
        private static double bootstrapMs;
        private static double finalizeInitMs;
        private static bool bootstrapped;

        internal static string SessionId => sessionId;
        internal static bool Active => active;
        internal static int ActiveClients => Math.Max(0, Volatile.Read(ref activeClients));
        internal static double BootstrapMs => bootstrapMs;
        internal static double HarmonyMs => harmonyMs;
        internal static double FinalizeInitMs => finalizeInitMs;
        internal static string WriteContext => Authorization.Context;

        internal static void Bootstrap(string modRoot)
        {
            if (bootstrapped) return;
            long start = Stopwatch.GetTimestamp();
            bootstrapped = true;
            BridgePaths.Initialize(modRoot);
            BridgeEventJournal.Record("lifecycle", "bootstrap");
            long harmonyStart = Stopwatch.GetTimestamp();
            Harmony harmony = new Harmony("lan.rimworld.devbridge.v2");
            harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)),
                postfix: new HarmonyMethod(typeof(BridgeRuntime), nameof(OnFinalizeInit)));
            harmony.Patch(AccessTools.PropertySetter(typeof(Current), nameof(Current.Game)),
                prefix: new HarmonyMethod(typeof(BridgeRuntime), nameof(OnGameChanging)));
            harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.Shutdown)),
                prefix: new HarmonyMethod(typeof(BridgeRuntime), nameof(Shutdown)));
            harmony.Patch(AccessTools.Method(typeof(Root), "Update"),
                postfix: new HarmonyMethod(typeof(BridgeRuntime), nameof(OnRootUpdate)));
            harmonyMs = BridgeTiming.Milliseconds(harmonyStart);
            StartDormantWatcher();
            WriteStatus("DORMANT");
            bootstrapMs = BridgeTiming.Milliseconds(start);
            bootstrapTicks = Stopwatch.GetTimestamp();
        }

        public static void OnFinalizeInit()
        {
            long start = Stopwatch.GetTimestamp();
            RotateSession("game");
            bool wakePending = File.Exists(BridgePaths.WakePath);
            if (wakePending)
            {
                TryDelete(BridgePaths.WakePath);
                StartTransport();
            }
            BridgeEventJournal.Record("lifecycle", "game finalized session:" + sessionId);
            WriteStatus(active ? "ON" : "DORMANT");
            finalizeInitMs = BridgeTiming.Milliseconds(start);
        }

        public static void OnRootUpdate()
        {
            MainThread.Drain(16, 4, exception =>
                Log.Error("[RimWorld Dev Bridge] Main-thread callback failed: " + exception));
        }

        public static void OnGameChanging(Game value)
        {
            if (ReferenceEquals(value, Current.Game)) return;
            RotateSession(value == null ? "menu" : "loading");
            BridgeEventJournal.Record("lifecycle", value == null ? "main menu" : "game changing");
        }

        public static void Shutdown()
        {
            shuttingDown = true;
            BridgeEventJournal.Record("lifecycle", "shutdown");
            RotateSession("shutdown");
            StopTransport(false);
            try { watcher?.Dispose(); } catch { }
            watcher = null;
            TryDelete(BridgePaths.WakePath);
            TryDelete(BridgePaths.InputPath);
            TryDelete(BridgePaths.StatusPath);
        }

        internal static BridgeResult SchedulerMetrics() => Scheduler.Metrics();

        internal static BridgeResult AcquireWriteLease(string context)
        {
            return Authorization.Acquire(context, Settings.RemoteMutationEnabled);
        }

        internal static void RefreshStatus()
        {
            WriteStatus(active ? "ON" : "DORMANT");
        }

        private static BridgeSettings Settings => RimWorldDevBridgeMod.Settings ?? new BridgeSettings();

        private static void RotateSession(string prefix)
        {
            string next = prefix + "-" + Guid.NewGuid().ToString("N");
            sessionId = next;
            Authorization.RotateSession(next);
            Scheduler.Configure(MainThread, next, Settings.QueueCapacity, Settings.MainThreadBudgetMs);
            Scheduler.RotateSession(next);
            token = active ? Guid.NewGuid().ToString("N") : string.Empty;
        }

        private static void StartDormantWatcher()
        {
            string saveFolder = GenFilePaths.SaveDataFolderPath;
            watcher = new FileSystemWatcher(saveFolder, BridgePaths.Prefix + "*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            watcher.Created += OnWakeFile;
            watcher.Changed += OnWakeFile;
            watcher.Renamed += OnWakeFile;
        }

        private static void OnWakeFile(object sender, FileSystemEventArgs args)
        {
            if (shuttingDown) return;
            string name = Path.GetFileName(args.FullPath);
            if (name.Equals(Path.GetFileName(BridgePaths.WakePath), StringComparison.OrdinalIgnoreCase))
            {
                MainThread.Post(_ =>
                {
                    TryDelete(BridgePaths.WakePath);
                    StartTransport();
                }, null);
            }
            else if (name.Equals(Path.GetFileName(BridgePaths.InputPath), StringComparison.OrdinalIgnoreCase))
                ThreadPool.QueueUserWorkItem(_ => ProcessLegacyFile());
        }

        private static void StartTransport()
        {
            if (shuttingDown) return;
            if (active && listener != null)
            {
                Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                return;
            }
            StopTransport(false);
            try
            {
                token = Guid.NewGuid().ToString("N");
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(Settings.ConnectedClientLimit);
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                active = true;
                int generation = Interlocked.Increment(ref transportGeneration);
                TcpListener current = listener;
                Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                BridgeAdapterCatalog.ActivateIndexing();
                listenerThread = new Thread(() => Listen(current, generation))
                {
                    IsBackground = true,
                    Name = "RimWorld Dev Bridge v2"
                };
                listenerThread.Start();
                idleTimer = new Timer(_ => CheckIdle(generation), null, 10000, 10000);
                WriteStatus("ON");
            }
            catch (Exception exception)
            {
                StopTransport(false);
                WriteStatus("DORMANT", "activationError=" + BridgeText.Clean(exception.GetBaseException().Message));
            }
        }

        private static void StopTransport(bool writeDormant)
        {
            active = false;
            Interlocked.Increment(ref transportGeneration);
            try { idleTimer?.Dispose(); } catch { }
            idleTimer = null;
            try { listener?.Stop(); } catch { }
            listener = null;
            port = 0;
            token = string.Empty;
            if (writeDormant && !shuttingDown) WriteStatus("DORMANT");
        }

        private static void CheckIdle(int generation)
        {
            if (!active || generation != transportGeneration) return;
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityUtcTicks) <
                TimeSpan.FromSeconds(IdleSeconds).Ticks) return;
            MainThread.Post(_ =>
            {
                if (generation == transportGeneration && ActiveClients == 0 &&
                    DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityUtcTicks) >=
                    TimeSpan.FromSeconds(IdleSeconds).Ticks) StopTransport(true);
            }, null);
        }

        private static void Listen(TcpListener current, int generation)
        {
            while (active && generation == transportGeneration)
            {
                TcpClient client = null;
                try
                {
                    client = current.AcceptTcpClient();
                    if (Interlocked.Increment(ref activeClients) > Settings.ConnectedClientLimit)
                    {
                        Interlocked.Decrement(ref activeClients);
                        WriteDirect(client, "id=unknown\nstatus=BUSY\nerror=connected_client_limit");
                        client = null;
                        continue;
                    }
                    TcpClient accepted = client;
                    client = null;
                    if (!ThreadPool.QueueUserWorkItem(_ => HandleClient(accepted)))
                    {
                        accepted.Close();
                        Interlocked.Decrement(ref activeClients);
                    }
                }
                catch (SocketException) { if (!active || generation != transportGeneration) return; }
                catch (ObjectDisposedException) { return; }
                catch { try { client?.Close(); } catch { } }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            BridgeRequest request = null;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = BridgeProtocol.MaximumDeadlineMs;
                client.SendTimeout = BridgeProtocol.MaximumDeadlineMs;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)
                { AutoFlush = true, NewLine = "\n" })
                {
                    string raw = ReadBoundedLine(reader, BridgeProtocol.MaxRequestBytes);
                    string prefix = token + "|";
                    if (string.IsNullOrEmpty(raw) || !raw.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=authentication_failed");
                        return;
                    }
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                    if (!BridgeProtocol.TryParse(raw.Substring(prefix.Length), sessionId, out request,
                        out BridgeResult parseFailure))
                    {
                        Decorate(parseFailure, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(parseFailure, "line"));
                        return;
                    }
                    if (request.Command == "CANCEL")
                    {
                        BridgeResult cancelled = BridgeResult.Ok("core.cancel")
                            .Add("cancelled", Scheduler.Cancel(request.Argument));
                        Decorate(cancelled, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(cancelled, request.OutputFormat));
                        return;
                    }
                    BridgeCommandDescriptor descriptor = BridgeDispatch.Describe(request);
                    if (descriptor == null)
                    {
                        BridgeResult unavailable = BridgeAdapterCatalog.Indexing
                            ? BridgeResult.Fail(BridgeStatus.BUSY, "adapter_indexing")
                            : BridgeResult.Fail(BridgeStatus.NOT_FOUND, "unknown_command");
                        Decorate(unavailable, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(unavailable, request.OutputFormat));
                        return;
                    }
                    request.Mode = descriptor.Mode;
                    request.Cost = descriptor.Cost;
                    request.PreparedDescriptor = descriptor.Clone();
                    BridgeResult prepareFailure = BridgeDispatch.Prepare(request);
                    if (prepareFailure != null)
                    {
                        Decorate(prepareFailure, request, descriptor.Provider, descriptor.ProviderVersion);
                        writer.Write(BridgeProtocol.Serialize(prepareFailure, request.OutputFormat));
                        return;
                    }
                    BridgeResult enqueueFailure = Scheduler.Enqueue(request);
                    if (enqueueFailure != null)
                    {
                        Decorate(enqueueFailure, request, descriptor.Provider, descriptor.ProviderVersion);
                        writer.Write(BridgeProtocol.Serialize(enqueueFailure, request.OutputFormat));
                        return;
                    }
                    while (!request.Done.Wait(20))
                    {
                        if (request.Expired)
                        {
                            request.Cancelled = true;
                            if (!request.Started) Scheduler.Cancel(request.RequestId);
                        }
                        if (Disconnected(client))
                        {
                            request.ClientDisconnected = true;
                            if (!request.Started) Scheduler.Cancel(request.RequestId);
                            return;
                        }
                    }
                    writer.Write(BridgeProtocol.Serialize(request.Result, request.OutputFormat));
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch { if (request != null && !request.Started) request.ClientDisconnected = true; }
            finally { Interlocked.Decrement(ref activeClients); }
        }

        private static BridgeResult ExecuteScheduled(BridgeRequest request)
        {
            BridgeCommandDescriptor descriptor = request.PreparedDescriptor ?? BridgeDispatch.Describe(request);
            if (descriptor == null) return Decorate(BridgeResult.Fail(BridgeStatus.NOT_FOUND,
                "unknown_command"), request, "core", BridgeProtocol.BridgeVersion);
            if (request.SessionId != sessionId)
                return Decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_session"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (descriptor.RequiresMap && Find.CurrentMap == null)
                return Decorate(BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "map_required"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (request.Remaining.TotalMilliseconds < descriptor.MinimumExecutionBudgetMs)
                return Decorate(BridgeResult.Fail(BridgeStatus.TIMEOUT, "insufficient_execution_budget")
                    .Add("requiredMs", descriptor.MinimumExecutionBudgetMs), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            BridgeResult authorization = Authorization.Authorize(request, descriptor, request.AuthToken,
                Settings.RemoteMutationEnabled);
            if (authorization != null)
                return Decorate(authorization, request, descriptor.Provider, descriptor.ProviderVersion);
            if (Authorization.TryGetCompleted(request, out BridgeResult cached))
                return Decorate(cached, request, descriptor.Provider, descriptor.ProviderVersion);
            if (request.Expired || request.Cancelled || request.ClientDisconnected)
                return Decorate(BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT : BridgeStatus.CANCELLED,
                    request.Expired ? "deadline_expired" : "cancelled_before_execution"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            int tickBefore = Find.TickManager?.TicksGame ?? -1;
            BridgeExecutionContext context = new BridgeExecutionContext(request, Find.CurrentMap,
                () => request.Cancelled || request.ClientDisconnected);
            BridgeResult result = BridgeDispatch.Execute(context);
            if (result == null) result = BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            result.TickBefore = tickBefore;
            result.TickAfter = Find.TickManager?.TicksGame ?? -1;
            Decorate(result, request, descriptor.Provider, descriptor.ProviderVersion);
            Authorization.Remember(request, result);
            Authorization.Audit(request, result);
            BridgeMetrics.Record(descriptor, result);
            BridgeEventJournal.Record("command", request.Command + " status:" + result.Status +
                " provider:" + descriptor.Provider + " executionMs:" + result.ExecutionMs.ToString("0.###"));
            return result;
        }

        private static BridgeResult Decorate(BridgeResult result, BridgeRequest request, string provider,
            string providerVersion)
        {
            result = result ?? BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            result.RequestId = request?.RequestId ?? result.RequestId ?? "unknown";
            result.SessionId = request?.SessionId ?? sessionId;
            result.Command = request?.Command ?? result.Command ?? "unknown";
            result.Provider = provider ?? "core";
            result.ProviderVersion = providerVersion ?? BridgeProtocol.BridgeVersion;
            result.Mode = request?.Mode ?? BridgeCommandMode.PureRead;
            return result;
        }

        private static void ProcessLegacyFile()
        {
            if (!File.Exists(BridgePaths.InputPath)) return;
            try
            {
                string raw = File.ReadAllText(BridgePaths.InputPath).Trim();
                File.Delete(BridgePaths.InputPath);
                if (!BridgeProtocol.TryParse(raw, sessionId, out BridgeRequest request, out BridgeResult failure))
                {
                    AtomicWrite(BridgePaths.OutputPath, BridgeProtocol.Serialize(failure, "line"));
                    return;
                }
                BridgeCommandDescriptor descriptor = BridgeDispatch.Describe(request);
                if (descriptor == null)
                {
                    failure = BridgeResult.Fail(BridgeStatus.NOT_FOUND, "unknown_command");
                    Decorate(failure, request, "core", BridgeProtocol.BridgeVersion);
                    AtomicWrite(BridgePaths.OutputPath, BridgeProtocol.Serialize(failure, "line"));
                    return;
                }
                request.Mode = descriptor.Mode;
                request.Cost = descriptor.Cost;
                request.PreparedDescriptor = descriptor.Clone();
                BridgeResult prepare = BridgeDispatch.Prepare(request);
                if (prepare != null)
                {
                    Decorate(prepare, request, descriptor.Provider, descriptor.ProviderVersion);
                    AtomicWrite(BridgePaths.OutputPath, BridgeProtocol.Serialize(prepare, "line"));
                    return;
                }
                BridgeResult enqueue = Scheduler.Enqueue(request);
                if (enqueue != null)
                {
                    Decorate(enqueue, request, descriptor.Provider, descriptor.ProviderVersion);
                    AtomicWrite(BridgePaths.OutputPath, BridgeProtocol.Serialize(enqueue, "line"));
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    request.Done.Wait(Math.Max(50, (int)request.Remaining.TotalMilliseconds));
                    BridgeResult result = request.Result ?? BridgeResult.Fail(BridgeStatus.TIMEOUT, "file_request_timeout");
                    Decorate(result, request, descriptor.Provider, descriptor.ProviderVersion);
                    AtomicWrite(BridgePaths.OutputPath, BridgeProtocol.Serialize(result, "line"));
                });
            }
            catch { }
        }

        private static bool Disconnected(TcpClient client)
        {
            try { return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0; }
            catch { return true; }
        }

        private static string ReadBoundedLine(StreamReader reader, int maxBytes)
        {
            StringBuilder value = new StringBuilder();
            while (true)
            {
                int next = reader.Read();
                if (next < 0 || next == '\n') break;
                if (next != '\r') value.Append((char)next);
                if (value.Length > maxBytes)
                    throw new InvalidDataException("Request exceeds maximum bytes.");
            }
            if (Encoding.UTF8.GetByteCount(value.ToString()) > maxBytes)
                throw new InvalidDataException("Request exceeds maximum bytes.");
            return value.ToString();
        }

        private static void WriteDirect(TcpClient client, string response)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(response);
            }
            catch { try { client?.Close(); } catch { } }
        }

        private static void WriteStatus(string state, string extra = null)
        {
            List<string> lines = new List<string>
            {
                "bridge=" + state,
                "name=RimWorld Dev Bridge",
                "version=" + BridgeProtocol.BridgeVersion,
                "protocol=" + BridgeProtocol.ProtocolVersion,
                "schema=" + BridgeProtocol.CoreSchema,
                "processId=" + Process.GetCurrentProcess().Id,
                "bootId=" + BootId,
                "session=" + sessionId,
                "transport=" + (active ? "tcp+file" : "wake-file"),
                "host=127.0.0.1",
                "port=" + port,
                "token=" + token,
                "clients=" + ActiveClients + "/" + Settings.ConnectedClientLimit,
                "context=" + Authorization.Context,
                "bootstrapMs=" + bootstrapMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "harmonyMs=" + harmonyMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "finalizeInitMs=" + finalizeInitMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "adapterIndex=" + BridgeAdapterCatalog.State,
                "input=" + BridgePaths.InputPath,
                "output=" + BridgePaths.OutputPath
            };
            if (!string.IsNullOrEmpty(extra)) lines.Add(extra);
            AtomicWrite(BridgePaths.StatusPath, string.Join("\n", lines));
        }

        private static void AtomicWrite(string path, string content)
        {
            try
            {
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    internal static class BridgeMetrics
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CommandMetric> Values =
            new Dictionary<string, CommandMetric>(StringComparer.OrdinalIgnoreCase);

        internal static void Record(BridgeCommandDescriptor descriptor, BridgeResult result)
        {
            lock (Gate)
            {
                if (!Values.TryGetValue(descriptor.Name, out CommandMetric metric))
                    Values[descriptor.Name] = metric = new CommandMetric();
                metric.Count++;
                metric.TotalMs += result.ExecutionMs;
                metric.MaxMs = Math.Max(metric.MaxMs, result.ExecutionMs);
                metric.LastStatus = result.Status;
                if (!result.IsSuccess) metric.Failures++;
            }
        }

        internal static BridgeResult Report()
        {
            BridgeResult result = BridgeResult.Ok("core.commandMetrics");
            lock (Gate)
            {
                foreach (KeyValuePair<string, CommandMetric> pair in Values.OrderByDescending(value => value.Value.MaxMs))
                    result.AddLine("command=" + pair.Key + " calls:" + pair.Value.Count + " failures:" +
                        pair.Value.Failures + " meanMs:" + (pair.Value.Count > 0 ? pair.Value.TotalMs / pair.Value.Count : 0d)
                            .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " maxMs:" +
                        pair.Value.MaxMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                        " last:" + pair.Value.LastStatus);
            }
            return result;
        }

        private sealed class CommandMetric
        {
            internal long Count;
            internal long Failures;
            internal double TotalMs;
            internal double MaxMs;
            internal BridgeStatus LastStatus;
        }
    }
}
