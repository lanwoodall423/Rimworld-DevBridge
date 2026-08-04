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
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeRuntime
    {
        private const int IdleSeconds = 180;
        private static readonly object Gate = new object();
        private static readonly object StatusGate = new object();
        private static readonly BridgeAuthorization Authorization = new BridgeAuthorization();
        private static readonly BridgeScheduler Scheduler = new BridgeScheduler(ExecuteScheduled, CompleteScheduled);
        private static readonly string BootId = Guid.NewGuid().ToString("N");
        private static readonly BridgeMainThreadContext MainThread = new BridgeMainThreadContext();
        private static readonly BridgeWakeSignal WakeSignal = new BridgeWakeSignal();
        private static readonly BridgeWakeSignal InputSignal = new BridgeWakeSignal();
        private static readonly Harmony Harmony = new Harmony("lan.rimworld.devbridge.v2");
        private const string WakeFileName = BridgePaths.Prefix + "Wake.request";
        private const string InputFileName = BridgePaths.Prefix + "In.txt";
        private static FileSystemWatcher watcher;
        private static TcpListener listener;
        private static Thread listenerThread;
        private static Timer idleTimer;
        private static volatile bool active;
        private static volatile bool transportReady;
        private static volatile bool activationIndexStarted;
        private static volatile bool legacyInputPending;
        private static volatile bool shuttingDown;
        private static bool updatePatched;
        private static int transportGeneration;
        private static long gameTransitionSequence;
        private static long publishedGameTransitionSequence;
        private static int publishedGameTransitionThreadId;
        private static int activeClients;
        private static int port;
        private static volatile SessionIdentity identity =
            new SessionIdentity("menu-" + Guid.NewGuid().ToString("N"), string.Empty);
        private static long lastActivityUtcTicks;
        private static long activationStartTicks;
        private static double harmonyMs;
        private static double bootstrapMs;
        private static double finalizeInitMs;
        private static double activationMs;
        private static double statusWriteMs;
        private static long bootstrapManagedDeltaBytes;
        private static bool bootstrapped;

        internal static bool Active => active;
        internal static int ActiveClients => Math.Max(0, Volatile.Read(ref activeClients));
        internal static double BootstrapMs => bootstrapMs;
        internal static double HarmonyMs => harmonyMs;
        internal static double FinalizeInitMs => finalizeInitMs;
        internal static double ActivationMs => activationMs;
        internal static long BootstrapManagedDeltaBytes => bootstrapManagedDeltaBytes;
        internal static string SessionId => identity.SessionId;
        internal static BridgeSessionContextSnapshot SessionContext
        {
            get
            {
                lock (Gate)
                {
                    BridgeSessionContextSnapshot authorization = Authorization.Snapshot();
                    return new BridgeSessionContextSnapshot(identity.SessionId, authorization.WriteContext,
                        authorization.RepresentativePlayerBehavior, authorization.WriteLeaseActive,
                        authorization.LeaseState, authorization.LeaseExpiresUtc);
                }
            }
        }
        internal static string WriteContext => SessionContext.WriteContext;
        internal static int EffectiveQueueCapacity => Scheduler.QueueCapacity;
        internal static int EffectiveMainThreadBudgetMs => Scheduler.MainThreadBudgetMs;
        internal static bool QueueCapacityPending => Settings.QueueCapacity != EffectiveQueueCapacity;
        internal static bool MainThreadBudgetPending => Settings.MainThreadBudgetMs != EffectiveMainThreadBudgetMs;
        internal static bool SchedulerSettingsPending => QueueCapacityPending || MainThreadBudgetPending;
        internal static long LifecycleSequenceForTests => Interlocked.Read(ref gameTransitionSequence);
        internal static long PublishedLifecycleSequenceForTests =>
            Interlocked.Read(ref publishedGameTransitionSequence);
        internal static int PublishedLifecycleThreadIdForTests =>
            Volatile.Read(ref publishedGameTransitionThreadId);

        internal static BridgeResult AddSessionContext(BridgeResult result, BridgeSessionContextSnapshot snapshot)
        {
            return result.Add("session", snapshot.SessionId)
                .Add("context", snapshot.WriteContext)
                .Add("writeContext", snapshot.WriteContext)
                .Add("representativePlayerBehavior", snapshot.RepresentativePlayerBehavior)
                .Add("writeLeaseActive", snapshot.WriteLeaseActive)
                .Add("leaseState", snapshot.LeaseState);
        }

        internal static void Bootstrap(string modRoot, long constructionStart, long managedBefore)
        {
            AssertMainThread("bootstrap");
            if (bootstrapped) return;
            bootstrapped = true;
            BridgePaths.Initialize(modRoot);
            Authorization.RotateSession(identity.SessionId);
            Scheduler.Configure(MainThread, identity.SessionId, Settings.QueueCapacity, Settings.MainThreadBudgetMs);
            long harmonyStart = Stopwatch.GetTimestamp();
            Harmony.Patch(AccessTools.PropertySetter(typeof(Current), nameof(Current.Game)),
                prefix: new HarmonyMethod(typeof(BridgeRuntime), nameof(OnGameChanging)));
            EnsureUpdatePatch();
            harmonyMs = BridgeTiming.Milliseconds(harmonyStart);
            UnityEngine.Application.quitting += Shutdown;
            StartDormantWatcher();
            bootstrapMs = BridgeTiming.Milliseconds(constructionStart);
            WriteStatus("DORMANT");
            bootstrapMs = BridgeTiming.Milliseconds(constructionStart);
            bootstrapManagedDeltaBytes = GC.GetTotalMemory(false) - managedBefore;
        }

        public static void OnFinalizeInit()
        {
            AssertMainThread("finalize initialization");
            long start = Stopwatch.GetTimestamp();
            RotateSession("game");
            bool wakePending = File.Exists(BridgePaths.WakePath);
            if (wakePending)
            {
                TryDelete(BridgePaths.WakePath);
                StartTransport();
            }
            if (File.Exists(BridgePaths.InputPath)) InputSignal.Signal();
            BridgeEventJournal.Record("lifecycle", "game finalized session:" + identity.SessionId);
            finalizeInitMs = BridgeTiming.Milliseconds(start);
            WriteStatus(active ? (transportReady ? "ON" : "ACTIVATING") : "DORMANT");
        }

        public static void OnRootUpdate()
        {
            MainThread.AdoptOwnerThread();
            AssertMainThread("root update");
            ProcessPendingFileSignals();
            RefreshIndicator();
            if (!active) return;
            if (BridgeQuerySnapshotStore.ActiveCount > 0)
                BridgeQuerySnapshotStore.CleanupStaleMaps((BridgeGameState.Maps ?? new List<Map>())
                    .Select(map => map.uniqueID));
            if (!transportReady)
            {
                if (!activationIndexStarted)
                {
                    activationIndexStarted = true;
                    BridgeAdapterCatalog.ActivateIndexing();
                }
                if (BridgeAdapterCatalog.Indexing)
                {
                    DrainMainThread();
                    return;
                }
                transportReady = true;
                activationMs = activationStartTicks == 0 ? 0d : BridgeTiming.Milliseconds(activationStartTicks);
                WriteStatus("ON");
            }
            if (legacyInputPending)
            {
                legacyInputPending = false;
                ProcessLegacyFile();
            }
            DrainMainThread();
        }

        public static void OnGameChanging(Game value)
        {
            // Verse can invoke this prefix from LongEventHandler worker threads. Keep the prefix
            // limited to managed, thread-safe invalidation; never touch Current.Game or UI state.
            try { BeginGameTransition(value == null); }
            catch { /* A prefix must never prevent Verse from assigning Current.Game. */ }
        }

        private static void BeginGameTransition(bool enteringMenu)
        {
            long sequence = Interlocked.Increment(ref gameTransitionSequence);
            string nextSession = (enteringMenu ? "menu" : "loading") + "-" + Guid.NewGuid().ToString("N");
            lock (Gate)
            {
                identity = new SessionIdentity(nextSession, string.Empty);
                active = false;
                transportReady = false;
                activationIndexStarted = false;
                legacyInputPending = false;
                Interlocked.Increment(ref transportGeneration);
                Authorization.RotateSession(nextSession);
                BridgeQuerySnapshotStore.RotateSession();
                Scheduler.RotateSession(nextSession);
            }

            // The Game object is intentionally not captured. The callback is sequence/session bound
            // so an older transition cannot publish state after a newer transition or finalization.
            MainThread.Post(_ => ApplyGameTransition(sequence, nextSession, enteringMenu), null);
        }

        private static void ApplyGameTransition(long sequence, string expectedSession, bool enteringMenu)
        {
            if (shuttingDown || sequence != Interlocked.Read(ref gameTransitionSequence) ||
                !string.Equals(SessionId, expectedSession, StringComparison.Ordinal)) return;
            AssertMainThread("game transition publication");
            if (sequence != Interlocked.Read(ref gameTransitionSequence) ||
                !string.Equals(SessionId, expectedSession, StringComparison.Ordinal)) return;
            Volatile.Write(ref publishedGameTransitionThreadId, Thread.CurrentThread.ManagedThreadId);
            Interlocked.Exchange(ref publishedGameTransitionSequence, sequence);
            StopTransport(false);
            if (sequence != Interlocked.Read(ref gameTransitionSequence) ||
                !string.Equals(SessionId, expectedSession, StringComparison.Ordinal)) return;
            BridgeEventJournal.Record("lifecycle", (enteringMenu ? "main menu" : "game changing") +
                " sequence:" + sequence);
            WriteStatus("DORMANT");
        }

        internal static int DrainMainThreadForTests()
        {
            AssertMainThread("test main-thread drain");
            return MainThread.Drain(64, 1000);
        }

        public static void Shutdown()
        {
            AssertMainThread("shutdown");
            shuttingDown = true;
            UnityEngine.Application.quitting -= Shutdown;
            BridgeEventJournal.Record("lifecycle", "shutdown");
            RotateSession("shutdown");
            StopTransport(false);
            BridgeIndicator.Close();
            RemoveUpdatePatch();
            try { watcher?.Dispose(); } catch { }
            watcher = null;
            TryDelete(BridgePaths.WakePath);
            TryDelete(BridgePaths.InputPath);
            TryDelete(BridgePaths.StatusPath);
        }

        internal static BridgeResult SchedulerMetrics() => Scheduler.Metrics();

        internal static BridgeResult AcquireWriteLease(string context)
        {
            BridgeResult result = Authorization.Acquire(context, Settings.RemoteMutationEnabled);
            RefreshIndicator();
            return result;
        }

        internal static void RefreshIndicator()
        {
            AssertMainThread("bridge indicator refresh");
            BridgeIndicator.Refresh(active, ActiveClients, Settings.ConnectedClientLimit,
                SessionContext, Settings.ShowBridgeIndicator, Settings.BridgeIndicatorCorner);
        }

        internal static void RefreshStatus()
        {
            AssertMainThread("status refresh");
            WriteStatus(active ? (transportReady ? "ON" : "ACTIVATING") : "DORMANT");
        }

        internal static void ApplySchedulerSettings()
        {
            AssertMainThread("scheduler reconfiguration");
            if (!bootstrapped) return;
            Scheduler.Reconfigure(Settings.QueueCapacity, Settings.MainThreadBudgetMs);
        }

        internal static void PostToMainThread(Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            MainThread.Post(_ => callback(), null);
        }

        private static void DrainMainThread()
        {
            MainThread.Drain(16, Scheduler.MainThreadBudgetMs, exception =>
                Log.Error("[RimWorld Dev Bridge] Main-thread callback failed: " + exception));
        }

        private static BridgeSettings Settings => RimWorldDevBridgeMod.Settings ?? new BridgeSettings();

        private static void RotateSession(string prefix)
        {
            AssertMainThread("session rotation");
            lock (Gate)
            {
                string next = prefix + "-" + Guid.NewGuid().ToString("N");
                Authorization.RotateSession(next);
                BridgeQuerySnapshotStore.RotateSession();
                Scheduler.Configure(MainThread, next, Settings.QueueCapacity, Settings.MainThreadBudgetMs);
                Scheduler.RotateSession(next);
                identity = new SessionIdentity(next, active ? Guid.NewGuid().ToString("N") : string.Empty);
            }
            RefreshIndicator();
        }

        private static void StartDormantWatcher()
        {
            AssertMainThread("watcher initialization");
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
            if (name.Equals(WakeFileName, StringComparison.OrdinalIgnoreCase)) WakeSignal.Signal();
            else if (name.Equals(InputFileName, StringComparison.OrdinalIgnoreCase)) InputSignal.Signal();
        }

        private static void ProcessPendingFileSignals()
        {
            AssertMainThread("file activation");
            if (WakeSignal.Consume())
            {
                TryDelete(BridgePaths.WakePath);
                StartTransport();
            }
            if (InputSignal.Consume()) legacyInputPending = true;
            if (legacyInputPending && !active) StartTransport();
        }

        private static void StartTransport()
        {
            AssertMainThread("transport activation");
            lock (Gate)
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
                    SessionIdentity currentIdentity = identity;
                    identity = new SessionIdentity(currentIdentity.SessionId, Guid.NewGuid().ToString("N"));
                    activationStartTicks = Stopwatch.GetTimestamp();
                    listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start(Settings.ConnectedClientLimit);
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    active = true;
                    transportReady = false;
                    activationIndexStarted = false;
                    int generation = Interlocked.Increment(ref transportGeneration);
                    TcpListener current = listener;
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                    listenerThread = new Thread(() => Listen(current, generation))
                    {
                        IsBackground = true,
                        Name = "RimWorld Dev Bridge v2"
                    };
                    listenerThread.Start();
                    idleTimer = new Timer(_ => CheckIdle(generation), null, 10000, 10000);
                    RefreshIndicator();
                    WriteStatus("ACTIVATING");
                }
                catch (Exception exception)
                {
                    StopTransport(false);
                    WriteStatus("DORMANT", "activationError=" + BridgeText.Clean(exception.GetBaseException().Message));
                }
            }
        }

        private static void StopTransport(bool writeDormant)
        {
            AssertMainThread("transport stop");
            lock (Gate)
            {
                active = false;
                transportReady = false;
                activationIndexStarted = false;
                Interlocked.Increment(ref transportGeneration);
                try { idleTimer?.Dispose(); } catch { }
                idleTimer = null;
                try { listener?.Stop(); } catch { }
                listener = null;
                port = 0;
                SessionIdentity currentIdentity = identity;
                identity = new SessionIdentity(currentIdentity.SessionId, string.Empty);
                RefreshIndicator();
                if (writeDormant && !shuttingDown) WriteStatus("DORMANT");
            }
        }

        private static void EnsureUpdatePatch()
        {
            AssertMainThread("update patch");
            if (updatePatched) return;
            Harmony.Patch(AccessTools.Method(typeof(Root), "Update"),
                postfix: new HarmonyMethod(typeof(BridgeRuntime), nameof(OnRootUpdate)));
            updatePatched = true;
        }

        private static void RemoveUpdatePatch()
        {
            AssertMainThread("update unpatch");
            if (!updatePatched) return;
            try
            {
                Harmony.Unpatch(AccessTools.Method(typeof(Root), "Update"), HarmonyPatchType.Postfix, Harmony.Id);
                updatePatched = false;
            }
            catch { }
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
                        RequestIndicatorRefresh();
                    }
                    else
                    {
                        RequestIndicatorRefresh();
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
                    string raw;
                    try { raw = ReadBoundedLine(reader, BridgeProtocol.MaxRequestBytes); }
                    catch (InvalidDataException exception)
                    {
                        BridgeResult invalid = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT,
                            "request_too_large", exception.Message);
                        Decorate(invalid, null, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(invalid, "line"));
                        return;
                    }
                    SessionIdentity acceptedIdentity = identity;
                    string prefix = acceptedIdentity.Token + "|";
                    if (string.IsNullOrEmpty(raw) || !raw.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=authentication_failed");
                        return;
                    }
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                    if (!BridgeProtocol.TryParse(raw.Substring(prefix.Length), acceptedIdentity.SessionId, out request,
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
                    long prepareStart = Stopwatch.GetTimestamp();
                    BridgeResult prepareFailure = PrepareRequestOnMainThread(request, out BridgeCommandDescriptor descriptor);
                    request.PreparationMs = BridgeTiming.Milliseconds(prepareStart);
                    if (descriptor == null)
                    {
                        BridgeResult unavailable = prepareFailure ?? (BridgeAdapterCatalog.Indexing
                            ? BridgeResult.Fail(BridgeStatus.BUSY, "adapter_indexing")
                            : BridgeResult.Fail(BridgeStatus.NOT_FOUND, "unknown_command"));
                        Decorate(unavailable, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(unavailable, request.OutputFormat));
                        return;
                    }
                    if (prepareFailure != null)
                    {
                        Decorate(prepareFailure, request, descriptor.Provider, descriptor.ProviderVersion);
                        writer.Write(BridgeProtocol.Serialize(prepareFailure, request.OutputFormat));
                        return;
                    }
                    request.EnqueuedUtc = DateTime.UtcNow;
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
            finally
            {
                Interlocked.Decrement(ref activeClients);
                RequestIndicatorRefresh();
            }
        }

        private static void RequestIndicatorRefresh()
        {
            try
            {
                MainThread.Post(_ =>
                {
                    if (!shuttingDown) RefreshIndicator();
                }, null);
            }
            catch { }
        }

        private static BridgeResult PrepareRequestOnMainThread(BridgeRequest request,
            out BridgeCommandDescriptor descriptor)
        {
            descriptor = null;
            if (MainThread.IsOwnerThread) return PrepareRequest(request, out descriptor);

            TaskCompletionSource<PreparationResult> completion =
                new TaskCompletionSource<PreparationResult>();
            try
            {
                MainThread.Post(_ =>
                {
                    BridgeCommandDescriptor preparedDescriptor = null;
                    BridgeResult failure;
                    try
                    {
                    failure = request.SessionId != SessionId
                        ? BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_session")
                        : request.Expired || request.Cancelled
                            ? BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT : BridgeStatus.CANCELLED,
                                request.Expired ? "deadline_expired" : "cancelled_before_prepare")
                        : PrepareRequest(request, out preparedDescriptor);
                    }
                    catch (Exception exception)
                    {
                        failure = BridgeResult.Fail(BridgeStatus.ERROR, "main_thread_prepare_failed",
                            exception.GetBaseException().Message);
                    }
                    completion.TrySetResult(new PreparationResult(preparedDescriptor, failure));
                }, null);
            }
            catch (Exception exception)
            {
                return BridgeResult.Fail(BridgeStatus.ERROR, "main_thread_prepare_failed",
                    exception.GetBaseException().Message);
            }

            int waitMs = Math.Max(1, Math.Min(int.MaxValue,
                (int)Math.Ceiling(Math.Max(1d, request.Remaining.TotalMilliseconds))));
            if (!completion.Task.Wait(waitMs))
            {
                request.Cancelled = true;
                return BridgeResult.Fail(BridgeStatus.TIMEOUT, "main_thread_prepare_timeout");
            }
            PreparationResult result = completion.Task.Result;
            descriptor = result.Descriptor;
            return result.Failure;
        }

        private static BridgeResult PrepareRequest(BridgeRequest request, out BridgeCommandDescriptor descriptor)
        {
            if (request.Expired)
            {
                descriptor = null;
                return BridgeResult.Fail(BridgeStatus.TIMEOUT, "deadline_expired");
            }
            descriptor = BridgeDispatch.Describe(request);
            if (descriptor == null) return null;
            request.Mode = descriptor.Mode;
            request.Cost = descriptor.Cost;
            request.PreparedDescriptor = descriptor.Clone();
            return BridgeDispatch.Prepare(request);
        }

        private static BridgeResult ExecuteScheduled(BridgeRequest request)
        {
            BridgeCommandDescriptor descriptor = request.PreparedDescriptor ?? BridgeDispatch.Describe(request);
            if (descriptor == null) return Decorate(BridgeResult.Fail(BridgeStatus.NOT_FOUND,
                "unknown_command"), request, "core", BridgeProtocol.BridgeVersion);
            if (request.SessionId != identity.SessionId)
                return Decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_session"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            BridgeResult authorization = Authorization.Authorize(request, descriptor, request.AuthToken,
                Settings.RemoteMutationEnabled);
            if (authorization != null)
                return Decorate(authorization, request, descriptor.Provider, descriptor.ProviderVersion);
            if (Authorization.TryGetCompleted(request, out BridgeResult cached))
                return Decorate(cached, request, descriptor.Provider, descriptor.ProviderVersion);
            if (descriptor.RequiresMap && BridgeGameState.CurrentMap == null)
                return Decorate(BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "map_required"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (request.Remaining.TotalMilliseconds < descriptor.MinimumExecutionBudgetMs)
                return Decorate(BridgeResult.Fail(BridgeStatus.TIMEOUT, "insufficient_execution_budget")
                    .Add("requiredMs", descriptor.MinimumExecutionBudgetMs), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (request.Expired || request.Cancelled || request.ClientDisconnected)
                return Decorate(BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT : BridgeStatus.CANCELLED,
                    request.Expired ? "deadline_expired" : "cancelled_before_execution"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            int tickBefore = BridgeGameState.TickManager?.TicksGame ?? -1;
            BridgeExecutionContext context = new BridgeExecutionContext(request, BridgeGameState.CurrentMap,
                () => request.Cancelled || request.ClientDisconnected);
            request.ExecutionReached = true;
            BridgeResult result = BridgeDispatch.Execute(context);
            if (request.Cancelled || request.ClientDisconnected || request.Expired ||
                request.SessionId != SessionId)
            {
                return Decorate(BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT :
                    request.SessionId != SessionId ? BridgeStatus.INCOMPATIBLE : BridgeStatus.CANCELLED,
                    request.Expired ? "execution_deadline_expired" :
                        request.SessionId != SessionId ? "stale_session" : "execution_cancelled"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            }
            if (result == null && request.YieldExecution) return null;
            if (result == null) result = BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            result.TickBefore = tickBefore;
            result.TickAfter = BridgeGameState.TickManager?.TicksGame ?? -1;
            Decorate(result, request, descriptor.Provider, descriptor.ProviderVersion);
            return result;
        }

        private static void CompleteScheduled(BridgeRequest request, BridgeResult result)
        {
            BridgeCommandDescriptor descriptor = request.PreparedDescriptor ?? BridgeDispatch.Describe(request);
            if (descriptor == null) return;
            bool currentSession = string.Equals(request.SessionId, SessionId, StringComparison.Ordinal);
            if (!request.IdempotentReplay && currentSession)
            {
                if (request.ExecutionReached) Authorization.Remember(request, result);
                Authorization.Audit(request, result);
            }
            BridgeMetrics.Record(descriptor, result);
            if (currentSession)
                BridgeEventJournal.Record("command", request.Command + " status:" + result.Status +
                    " provider:" + descriptor.Provider + " executionMs:" + result.ExecutionMs.ToString("0.###"));
        }

        private static BridgeResult Decorate(BridgeResult result, BridgeRequest request, string provider,
            string providerVersion)
        {
            result = result ?? BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            result.RequestId = request?.RequestId ?? result.RequestId ?? "unknown";
            result.SessionId = request?.SessionId ?? identity.SessionId;
            result.Command = request?.Command ?? result.Command ?? "unknown";
            result.Provider = provider ?? "core";
            result.ProviderVersion = providerVersion ?? BridgeProtocol.BridgeVersion;
            result.Mode = request?.Mode ?? BridgeCommandMode.PureRead;
            result.PreparationMs = request?.PreparationMs ?? result.PreparationMs;
            return result;
        }

        private static void ProcessLegacyFile()
        {
            AssertMainThread("legacy file processing");
            string inputPath = BridgePaths.InputPath;
            string outputPath = BridgePaths.OutputPath;
            if (!File.Exists(inputPath)) return;
            try
            {
                string raw = File.ReadAllText(inputPath).Trim();
                File.Delete(inputPath);
                if (!BridgeProtocol.TryParse(raw, identity.SessionId, out BridgeRequest request, out BridgeResult failure))
                {
                    AtomicWrite(outputPath, BridgeProtocol.Serialize(failure, "line"));
                    return;
                }
                long prepareStart = Stopwatch.GetTimestamp();
                BridgeResult prepare = PrepareRequest(request, out BridgeCommandDescriptor descriptor);
                if (descriptor == null)
                {
                    request.PreparationMs = BridgeTiming.Milliseconds(prepareStart);
                    failure = prepare ?? BridgeResult.Fail(BridgeStatus.NOT_FOUND, "unknown_command");
                    Decorate(failure, request, "core", BridgeProtocol.BridgeVersion);
                    AtomicWrite(outputPath, BridgeProtocol.Serialize(failure, "line"));
                    return;
                }
                request.PreparationMs = BridgeTiming.Milliseconds(prepareStart);
                if (prepare != null)
                {
                    Decorate(prepare, request, descriptor.Provider, descriptor.ProviderVersion);
                    AtomicWrite(outputPath, BridgeProtocol.Serialize(prepare, "line"));
                    return;
                }
                request.EnqueuedUtc = DateTime.UtcNow;
                BridgeResult enqueue = Scheduler.Enqueue(request);
                if (enqueue != null)
                {
                    Decorate(enqueue, request, descriptor.Provider, descriptor.ProviderVersion);
                    AtomicWrite(outputPath, BridgeProtocol.Serialize(enqueue, "line"));
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    request.Done.Wait(Math.Max(50, (int)request.Remaining.TotalMilliseconds));
                    BridgeResult result = request.Result ?? BridgeResult.Fail(BridgeStatus.TIMEOUT, "file_request_timeout");
                    Decorate(result, request, descriptor.Provider, descriptor.ProviderVersion);
                    AtomicWrite(outputPath, BridgeProtocol.Serialize(result, "line"));
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
            AssertMainThread("status write");
            lock (StatusGate)
            {
                long writeStart = Stopwatch.GetTimestamp();
                BridgeSessionContextSnapshot context = SessionContext;
                List<string> lines = new List<string>
                {
                    "bridge=" + state,
                    "name=RimWorld Dev Bridge",
                    "version=" + BridgeProtocol.BridgeVersion,
                    "protocol=" + BridgeProtocol.ProtocolVersion,
                    "schema=" + BridgeProtocol.CoreSchema,
                    "processId=" + Process.GetCurrentProcess().Id,
                    "bootId=" + BootId,
                    "session=" + context.SessionId,
                    "transport=" + (active ? "tcp+file" : "wake-file"),
                    "host=127.0.0.1",
                    "port=" + port,
                    "token=" + identity.Token,
                    "clients=" + ActiveClients + "/" + Settings.ConnectedClientLimit,
                    "context=" + context.WriteContext,
                    "writeContext=" + context.WriteContext,
                    "representativePlayerBehavior=" + context.RepresentativePlayerBehavior,
                    "writeLeaseActive=" + context.WriteLeaseActive,
                    "leaseState=" + context.LeaseState,
                    "bootstrapMs=" + bootstrapMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "harmonyMs=" + harmonyMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "finalizeInitMs=" + finalizeInitMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "activationMs=" + activationMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "statusWriteMs=" + statusWriteMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "bootstrapManagedDeltaBytesApprox=" + bootstrapManagedDeltaBytes,
                    "adapterIndex=" + BridgeAdapterCatalog.State,
                    "input=" + BridgePaths.InputPath,
                    "output=" + BridgePaths.OutputPath
                };
                if (!string.IsNullOrEmpty(extra)) lines.Add(extra);
                AtomicWrite(BridgePaths.StatusPath, string.Join("\n", lines));
                statusWriteMs = BridgeTiming.Milliseconds(writeStart);
            }
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

        private static void AssertMainThread(string operation)
        {
            MainThread.AssertOwnerThread(operation);
        }

        private sealed class SessionIdentity
        {
            internal readonly string SessionId;
            internal readonly string Token;

            internal SessionIdentity(string sessionId, string token)
            {
                SessionId = sessionId;
                Token = token;
            }
        }

        private sealed class PreparationResult
        {
            internal readonly BridgeCommandDescriptor Descriptor;
            internal readonly BridgeResult Failure;

            internal PreparationResult(BridgeCommandDescriptor descriptor, BridgeResult failure)
            {
                Descriptor = descriptor;
                Failure = failure;
            }
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
                metric.MaxStepMs = Math.Max(metric.MaxStepMs, result.MaxMainThreadStepMs);
                if (result.MainThreadOverrun) metric.Overruns++;
                metric.CooperativeSteps += result.CooperativeSteps;
                if (descriptor.NonCooperative || result.NonCooperativeExecution) metric.NonCooperative = true;
                if (descriptor.Cooperative) metric.Cooperative = true;
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
                        " maxStepMs:" + pair.Value.MaxStepMs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                        " overruns:" + pair.Value.Overruns + " cooperativeSteps:" + pair.Value.CooperativeSteps +
                        " contract:" + (pair.Value.NonCooperative ? "legacy-sync-non-cooperative" :
                            pair.Value.Cooperative ? "cooperative-v1" : "sync") +
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
            internal double MaxStepMs;
            internal long Overruns;
            internal long CooperativeSteps;
            internal bool NonCooperative;
            internal bool Cooperative;
            internal BridgeStatus LastStatus;
        }
    }
}
