using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
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
        private static readonly object LeaseTimerGate = new object();
        private static readonly BridgeAuthorization Authorization = new BridgeAuthorization();
        private static readonly BridgeMutationConfirmation MutationConfirmation =
            new BridgeMutationConfirmation();
        private static readonly BridgeScheduler Scheduler = new BridgeScheduler(ExecuteScheduled, CompleteScheduled);
        private static readonly string BootId = Guid.NewGuid().ToString("N");
        private static readonly BridgeMainThreadContext MainThread = new BridgeMainThreadContext();
        private static readonly BridgeWakeSignal WakeSignal = new BridgeWakeSignal();
        private static readonly BridgeWakeSignal InputSignal = new BridgeWakeSignal();
        private static readonly Harmony Harmony = new Harmony("lan.rimworld.devbridge.v2");
        private const string WakeFileName = BridgePaths.Prefix + "Wake.request";
        private const string InputFileName = BridgePaths.Prefix + "In.txt";
        private static FileSystemWatcher watcher;
        private static TransportState transportState;
        private static volatile bool active;
        private static volatile bool transportReady;
        private static volatile bool activationIndexStarted;
        private static volatile bool legacyInputPending;
        private static volatile bool shuttingDown;
        private static bool updatePatched;
        private static int transportGeneration;
        private static long gameTransitionSequence;
        private static long publishedGameTransitionSequence;
        private static long stateVersion;
        private static int stateDirty = 1;
        private static int publishedGameTransitionThreadId;
        private static int port;
        private static volatile string statusPath;
        private static volatile SessionIdentity identity =
            new SessionIdentity("menu-" + Guid.NewGuid().ToString("N"), string.Empty);
        private static long lastActivityUtcTicks;
        private static long activationStartTicks;
        private static double harmonyMs;
        private static double bootstrapMs;
        private static double finalizeInitMs;
        private static double activationMs;
        private static double statusWriteMs;
        private static int statusWriteCount;
        private static long bootstrapManagedDeltaBytes;
        private static string coreFingerprint;
        private static bool bootstrapped;
        private static Timer leaseExpiryTimer;
        private static long leaseExpiryTicks;
        private static string leaseExpirySession;

        internal static bool Active => active;
        internal static int ActiveClients
        {
            get
            {
                TransportState current = Volatile.Read(ref transportState);
                return current == null ? 0 : Math.Max(0, Volatile.Read(ref current.ActiveClients));
            }
        }
        internal static double BootstrapMs => bootstrapMs;
        internal static double HarmonyMs => harmonyMs;
        internal static double FinalizeInitMs => finalizeInitMs;
        internal static double ActivationMs => activationMs;
        internal static long BootstrapManagedDeltaBytes => bootstrapManagedDeltaBytes;
        internal static string SessionId => identity.SessionId;
        internal static string BootIdForClients => BootId;
        internal static int ProcessIdForClients => Process.GetCurrentProcess().Id;
        internal static string CoreFingerprint => coreFingerprint ?? (coreFingerprint = ComputeCoreFingerprint());
        internal static BridgeRuntimeStateSnapshot StateSnapshot => CaptureStateSnapshot();
        internal static BridgeSessionContextSnapshot SessionContext => StateSnapshot.Context;
        internal static int StatusWriteCountForTests => Volatile.Read(ref statusWriteCount);
        internal static int IndicatorRefreshCountForTests => BridgeIndicator.RefreshCountForTests;
        internal static long StateVersionForTests => Interlocked.Read(ref stateVersion);
        internal static void ResetStatePublicationCountersForTests()
        {
            Interlocked.Exchange(ref statusWriteCount, 0);
            BridgeIndicator.ResetRefreshCountForTests();
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
        internal static int TransportGenerationForTests => Volatile.Read(ref transportGeneration);
        internal static int TransportResourceGenerationForTests
        {
            get
            {
                TransportState current = Volatile.Read(ref transportState);
                return current == null ? 0 : current.Generation;
            }
        }
        internal static int TransportPortForTests => Volatile.Read(ref port);
        internal static void SignalWakeForTests() => WakeSignal.Signal();
        internal static void CaptureStatusPathForTests() => statusPath = BridgePaths.StatusPath;
        internal static void BindCurrentGameForTests(Game game)
        {
            AssertMainThread("test game confirmation binding");
            MutationConfirmation.BindCurrentGame(SessionId, game);
            MarkStateDirty();
            PublishStateIfDirty(true);
        }
        internal static bool AuthenticateForTests(string raw, string expectedToken, out string payload) =>
            TrySplitAuthentication(raw, expectedToken, out payload);

        internal static BridgeResult AddSessionContext(BridgeResult result, BridgeSessionContextSnapshot snapshot)
        {
            return result.Add("session", snapshot.SessionId)
                .Add("context", snapshot.WriteContext)
                .Add("writeContext", snapshot.WriteContext)
                .Add("representativePlayerBehavior", snapshot.RepresentativePlayerBehavior)
                .Add("writeLeaseActive", snapshot.WriteLeaseActive)
                .Add("leaseState", snapshot.LeaseState)
                .Add("leaseExpiresUtc", snapshot.LeaseExpiresUtc?.ToString("o",
                    System.Globalization.CultureInfo.InvariantCulture) ?? "none");
        }

        internal static BridgeResult AddSessionContext(BridgeResult result, BridgeRuntimeStateSnapshot snapshot)
        {
            return AddSessionContext(result, snapshot.Context)
                .Add("remoteMutationEnabled", snapshot.RemoteMutationEnabled)
                .Add("mutationConfirmation", snapshot.MutationConfirmation.State)
                .Add("mutationGameLoaded", snapshot.MutationConfirmation.GameLoaded)
                .Add("mutationConfirmed", snapshot.MutationConfirmation.Confirmed);
        }

        private static BridgeRuntimeStateSnapshot CaptureStateSnapshot()
        {
            lock (Gate)
            {
                BridgeSessionContextSnapshot context = Authorization.Snapshot();
                TransportState current = Volatile.Read(ref transportState);
                BridgeMutationConfirmationSnapshot confirmation = MutationConfirmation.Snapshot(
                    identity.SessionId, Settings.RemoteMutationEnabled);
                return new BridgeRuntimeStateSnapshot(
                    Interlocked.Read(ref stateVersion),
                    current != null && active,
                    current != null && active && transportReady,
                    current == null ? 0 : current.Generation,
                    current == null ? 0 : Math.Max(0, Volatile.Read(ref current.ActiveClients)),
                    Settings.ConnectedClientLimit,
                    Volatile.Read(ref port),
                    current?.Token ?? string.Empty,
                    context, Settings.RemoteMutationEnabled, confirmation);
            }
        }

        private static void MarkStateDirty()
        {
            Interlocked.Increment(ref stateVersion);
            Interlocked.Exchange(ref stateDirty, 1);
        }

        private static void PublishStateIfDirty(bool force = false, string extra = null)
        {
            AssertMainThread("bridge state publication");
            if (!force && Interlocked.Exchange(ref stateDirty, 0) == 0) return;
            Interlocked.Exchange(ref stateDirty, 0);
            BridgeRuntimeStateSnapshot snapshot = CaptureStateSnapshot();
            ScheduleLeaseExpiry(snapshot);
            BridgeIndicator.Refresh(snapshot, Settings.ShowBridgeIndicator, Settings.BridgeIndicatorCorner);
            string state = snapshot.TransportActive ?
                (snapshot.TransportReady ? "ON" : "ACTIVATING") : "DORMANT";
            if (!WriteStatus(snapshot, state, extra)) Interlocked.Exchange(ref stateDirty, 1);
        }

        internal static void Bootstrap(string modRoot, long constructionStart, long managedBefore)
        {
            AssertMainThread("bootstrap");
            if (bootstrapped) return;
            bootstrapped = true;
            BridgePaths.Initialize(modRoot);
            statusPath = BridgePaths.StatusPath;
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
            MarkStateDirty();
            PublishStateIfDirty(true);
            bootstrapMs = BridgeTiming.Milliseconds(constructionStart);
            bootstrapManagedDeltaBytes = GC.GetTotalMemory(false) - managedBefore;
        }

        public static void OnFinalizeInit()
        {
            AssertMainThread("finalize initialization");
            long start = Stopwatch.GetTimestamp();
            RotateSession("game");
            MutationConfirmation.BindCurrentGame(SessionId, Current.Game);
            bool wakePending = File.Exists(BridgePaths.WakePath);
            if (wakePending)
            {
                TryDelete(BridgePaths.WakePath);
                StartTransport();
            }
            if (File.Exists(BridgePaths.InputPath)) InputSignal.Signal();
            BridgeEventJournal.Record("lifecycle", "game finalized session:" + identity.SessionId);
            finalizeInitMs = BridgeTiming.Milliseconds(start);
            MarkStateDirty();
            PublishStateIfDirty(true);
        }

        public static void OnRootUpdate()
        {
            MainThread.AdoptOwnerThread();
            AssertMainThread("root update");
            ProcessPendingFileSignals();
            long scheduledLeaseExpiry = Interlocked.Read(ref leaseExpiryTicks);
            if (scheduledLeaseExpiry != 0 && scheduledLeaseExpiry <= DateTime.UtcNow.Ticks)
                MarkStateDirty();
            PublishStateIfDirty();
            if (!active)
            {
                // Lifecycle publication must still run while transport is inactive. Keep this
                // path limited to deferred callbacks; command and map work remains dormant.
                DrainMainThread();
                return;
            }
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
                MarkStateDirty();
                PublishStateIfDirty(true);
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
            TransportState staleTransport;
            lock (Gate)
            {
                identity = new SessionIdentity(nextSession, string.Empty);
                active = false;
                transportReady = false;
                activationIndexStarted = false;
                legacyInputPending = false;
                staleTransport = DetachTransportLocked();
                Authorization.RotateSession(nextSession);
                MutationConfirmation.Invalidate(nextSession);
                BridgeQuerySnapshotStore.RotateSession();
                Scheduler.RotateSession(nextSession);
                MarkStateDirty();
            }
            // Serialize removal with any in-flight status write. If a new generation was already
            // activated, its status publication owns the path and must not be deleted by this stale
            // transition.
            lock (StatusGate)
            {
                if (Volatile.Read(ref transportState) == null && !active) TryDelete(statusPath);
            }
            // Detachment above makes the state unreachable before any new activation can use the
            // generation. Close only the detached resources, outside Gate, to keep the worker prefix
            // from holding lifecycle locks while sockets/timers are being disposed.
            CloseTransportResources(staleTransport);
            CancelLeaseExpiryTimer();

            // The Game object is intentionally not captured. The callback is sequence/session bound
            // so an older transition cannot publish state after a newer transition or finalization.
            MainThread.Post(_ => ApplyGameTransition(sequence, nextSession, enteringMenu,
                staleTransport == null ? 0 : staleTransport.Generation), null);
        }

        private static void ApplyGameTransition(long sequence, string expectedSession, bool enteringMenu,
            int invalidatedGeneration)
        {
            if (shuttingDown || sequence != Interlocked.Read(ref gameTransitionSequence) ||
                !string.Equals(SessionId, expectedSession, StringComparison.Ordinal)) return;
            AssertMainThread("game transition publication");
            if (sequence != Interlocked.Read(ref gameTransitionSequence) ||
                !string.Equals(SessionId, expectedSession, StringComparison.Ordinal)) return;
            lock (Gate)
            {
                if (sequence != Interlocked.Read(ref gameTransitionSequence) ||
                    !string.Equals(SessionId, expectedSession, StringComparison.Ordinal)) return;
                TransportState current = Volatile.Read(ref transportState);
                if ((invalidatedGeneration != 0 && current != null) ||
                    (invalidatedGeneration == 0 && (current != null || active))) return;
                Volatile.Write(ref publishedGameTransitionThreadId, Thread.CurrentThread.ManagedThreadId);
                Interlocked.Exchange(ref publishedGameTransitionSequence, sequence);
                BridgeEventJournal.Record("lifecycle", (enteringMenu ? "main menu" : "game changing") +
                    " sequence:" + sequence);
                MarkStateDirty();
            }
            PublishStateIfDirty(true);
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

        internal static BridgeResult BeginRestartDrain(BridgeRequest request)
        {
            AssertMainThread("restart drain");
            long barrierId = Scheduler.BeginDrain();
            Authorization.ClearLeases();
            MutationConfirmation.Revoke();
            CancelLeaseExpiryTimer();
            MarkStateDirty();
            PublishStateIfDirty(true, "restartDrain=active barrierId=" + barrierId);
            return Scheduler.DrainStatus(request);
        }

        internal static BridgeResult RestartDrainStatus(BridgeRequest request) =>
            Scheduler.DrainStatus(request);

        internal static BridgeResult AcquireWriteLease(string context) => AcquireWriteLease(context, null);

        internal static BridgeResult AcquireWriteLease(string context, string agentId)
        {
            BridgeResult gate = RequireMutationConfirmation();
            if (gate != null) return gate;
            BridgeResult result = Authorization.Acquire(context, Settings.RemoteMutationEnabled, agentId);
            if (result.IsSuccess)
            {
                MarkStateDirty();
                PublishStateIfDirty(true);
            }
            return result;
        }

        internal static BridgeResult RenewWriteLease(string leaseToken) => RenewWriteLease(leaseToken, null);

        internal static BridgeResult RenewWriteLease(string leaseToken, string agentId)
        {
            BridgeResult gate = RequireMutationConfirmation();
            if (gate != null) return gate;
            BridgeResult result = Authorization.Renew(leaseToken, Settings.RemoteMutationEnabled, agentId);
            if (result.IsSuccess)
            {
                MarkStateDirty();
                PublishStateIfDirty(true);
            }
            return result;
        }

        internal static BridgeResult RevokeWriteLease(string leaseToken) => RevokeWriteLease(leaseToken, null);

        internal static BridgeResult RevokeWriteLease(string leaseToken, string agentId)
        {
            BridgeResult result = Authorization.Revoke(leaseToken, agentId);
            if (result.IsSuccess)
            {
                MarkStateDirty();
                PublishStateIfDirty(true);
            }
            return result;
        }

        internal static BridgeResult ConfirmMutationForCurrentGame()
        {
            AssertMainThread("remote mutation confirmation");
            if (!Settings.RemoteMutationEnabled)
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            if (Current.Game == null)
                return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "no_game_loaded");
            BridgeResult result = MutationConfirmation.Confirm(SessionId,
                BridgeMutationConfirmation.IdentityFor(Current.Game),
                BridgeMutationConfirmation.SaveIdentityFor(Current.Game));
            if (result.IsSuccess)
            {
                MarkStateDirty();
                PublishStateIfDirty(true);
            }
            return result;
        }

        internal static BridgeResult RevokeMutationConfirmation()
        {
            AssertMainThread("remote mutation revocation");
            MutationConfirmation.Revoke();
            Authorization.ClearLeases();
            CancelLeaseExpiryTimer();
            MarkStateDirty();
            PublishStateIfDirty(true);
            return BridgeResult.Ok("core.mutationConfirmationRevoked");
        }

        internal static void ApplyRemoteMutationSettings()
        {
            AssertMainThread("remote mutation setting");
            if (!Settings.RemoteMutationEnabled)
            {
                MutationConfirmation.Revoke();
                Authorization.ClearLeases();
                CancelLeaseExpiryTimer();
            }
            MarkStateDirty();
            PublishStateIfDirty(true);
        }

        private static BridgeResult RequireMutationConfirmation()
        {
            AssertMainThread("remote mutation authorization");
            if (!Settings.RemoteMutationEnabled)
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            if (Current.Game == null)
                return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "no_game_loaded");
            string gameIdentity = BridgeMutationConfirmation.IdentityFor(Current.Game);
            string saveIdentity = BridgeMutationConfirmation.SaveIdentityFor(Current.Game);
            if (!MutationConfirmation.IsConfirmed(SessionId, gameIdentity, saveIdentity))
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "in_game_confirmation_required",
                    "Confirm remote mutation in-game before requesting a write lease.");
            return null;
        }

        internal static void RefreshIndicator()
        {
            AssertMainThread("bridge indicator refresh");
            MarkStateDirty();
            PublishStateIfDirty(true);
        }

        internal static void RefreshStatus()
        {
            AssertMainThread("status refresh");
            MarkStateDirty();
            PublishStateIfDirty(true);
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

        private static void ScheduleLeaseExpiry(BridgeRuntimeStateSnapshot snapshot)
        {
            BridgeSessionContextSnapshot context = snapshot.Context;
            if (!context.WriteLeaseActive || !context.LeaseExpiresUtc.HasValue)
            {
                CancelLeaseExpiryTimer();
                return;
            }
            long expiry = context.LeaseExpiresUtc.Value.Ticks;
            lock (LeaseTimerGate)
            {
                if (leaseExpiryTimer != null && leaseExpiryTicks == expiry &&
                    string.Equals(leaseExpirySession, context.SessionId, StringComparison.Ordinal)) return;
                Timer previous = leaseExpiryTimer;
                leaseExpiryTimer = null;
                leaseExpiryTicks = expiry;
                leaseExpirySession = context.SessionId;
                previous?.Dispose();
                long remainingTicks = expiry - DateTime.UtcNow.Ticks;
                int dueMs = remainingTicks <= 0 ? 1 :
                    (int)Math.Min(int.MaxValue, Math.Max(1d,
                        TimeSpan.FromTicks(remainingTicks).TotalMilliseconds));
                string session = context.SessionId;
                long version = snapshot.Version;
                int generation = snapshot.TransportGeneration;
                leaseExpiryTimer = new Timer(_ => OnLeaseExpiry(session, expiry, version, generation), null, dueMs,
                    Timeout.Infinite);
            }
        }

        private static void OnLeaseExpiry(string session, long expiryTicks, long version, int generation)
        {
            if (shuttingDown) return;
            try
            {
                MainThread.Post(_ =>
                {
                    if (shuttingDown) return;
                    BridgeRuntimeStateSnapshot snapshot = CaptureStateSnapshot();
                    if (snapshot.Version != version || snapshot.TransportGeneration != generation)
                    {
                        ScheduleLeaseExpiry(snapshot);
                        return;
                    }
                    BridgeSessionContextSnapshot context = snapshot.Context;
                    if (context.WriteLeaseActive && context.LeaseExpiresUtc.HasValue &&
                        string.Equals(context.SessionId, session, StringComparison.Ordinal) &&
                        context.LeaseExpiresUtc.Value.Ticks == expiryTicks &&
                        context.LeaseExpiresUtc.Value > DateTime.UtcNow)
                    {
                        ScheduleLeaseExpiry(snapshot);
                        return;
                    }
                    MarkStateDirty();
                    PublishStateIfDirty(true);
                }, null);
            }
            catch { }
        }

        private static void CancelLeaseExpiryTimer()
        {
            Timer previous;
            lock (LeaseTimerGate)
            {
                previous = leaseExpiryTimer;
                leaseExpiryTimer = null;
                leaseExpiryTicks = 0;
                leaseExpirySession = null;
            }
            try { previous?.Dispose(); } catch { }
        }

        private static void RotateSession(string prefix)
        {
            AssertMainThread("session rotation");
            TransportState stale;
            lock (Gate)
            {
                string next = prefix + "-" + Guid.NewGuid().ToString("N");
                stale = DetachTransportLocked();
                Authorization.RotateSession(next);
                MutationConfirmation.Invalidate(next);
                BridgeQuerySnapshotStore.RotateSession();
                Scheduler.Configure(MainThread, next, Settings.QueueCapacity, Settings.MainThreadBudgetMs);
                Scheduler.RotateSession(next);
                identity = new SessionIdentity(next, string.Empty);
                MarkStateDirty();
            }
            CloseTransportResources(stale);
            CancelLeaseExpiryTimer();
            PublishStateIfDirty(true);
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
            TransportState stale = null;
            TransportState failedToClose = null;
            string activationError = null;
            bool started = false;
            lock (Gate)
            {
                if (shuttingDown) return;
                TransportState current = Volatile.Read(ref transportState);
                if (IsCurrentTransport(current))
                {
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                    return;
                }
                stale = DetachTransportLocked();
                try
                {
                    SessionIdentity currentIdentity = identity;
                    string token = Guid.NewGuid().ToString("N");
                    identity = new SessionIdentity(currentIdentity.SessionId, token);
                    activationStartTicks = Stopwatch.GetTimestamp();
                    int generation = Interlocked.Increment(ref transportGeneration);
                    TcpListener currentListener = new TcpListener(IPAddress.Loopback, 0);
                    currentListener.Start(Settings.ConnectedClientLimit);
                    port = ((IPEndPoint)currentListener.LocalEndpoint).Port;
                    TransportState next = new TransportState(generation, currentListener,
                        currentIdentity.SessionId, token);
                    transportState = next;
                    active = true;
                    transportReady = false;
                    activationIndexStarted = false;
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                    Thread listenerThread = new Thread(() => Listen(next))
                    {
                        IsBackground = true,
                        Name = "RimWorld Dev Bridge v2"
                    };
                    listenerThread.Start();
                    next.IdleTimer = new Timer(_ => CheckIdle(next), null, 10000, 10000);
                    MarkStateDirty();
                    started = true;
                }
                catch (Exception exception)
                {
                    failedToClose = DetachTransportLocked();
                    activationError = "activationError=" + BridgeText.Clean(exception.GetBaseException().Message);
                    MarkStateDirty();
                }
            }
            CloseTransportResources(stale);
            CloseTransportResources(failedToClose);
            if (started || activationError != null) PublishStateIfDirty(true, activationError);
        }

        private static void StopTransport(bool writeDormant, TransportState expectedState = null)
        {
            AssertMainThread("transport stop");
            TransportState stale;
            int detachedGeneration;
            lock (Gate)
            {
                if (expectedState != null && !ReferenceEquals(transportState, expectedState)) return;
                stale = DetachTransportLocked();
                detachedGeneration = Volatile.Read(ref transportGeneration);
            }
            CloseTransportResources(stale);
            bool publish = false;
            lock (Gate)
            {
                if (detachedGeneration != Volatile.Read(ref transportGeneration) ||
                    transportState != null || active) return;
                MarkStateDirty();
                publish = true;
            }
            if (publish) PublishStateIfDirty(true);
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

        private static void CheckIdle(TransportState state)
        {
            if (!IsCurrentTransport(state)) return;
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityUtcTicks) <
                TimeSpan.FromSeconds(IdleSeconds).Ticks) return;
            MainThread.Post(_ =>
            {
                if (IsCurrentTransport(state) && ActiveClients == 0 &&
                    DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityUtcTicks) >=
                    TimeSpan.FromSeconds(IdleSeconds).Ticks) StopTransport(true, state);
            }, null);
        }

        private static void Listen(TransportState state)
        {
            while (IsCurrentTransport(state))
            {
                TcpClient client = null;
                try
                {
                    client = state.Listener.AcceptTcpClient();
                    if (!IsCurrentTransport(state))
                    {
                        client.Close();
                        client = null;
                        return;
                    }
                    if (Interlocked.Increment(ref state.ActiveClients) > Settings.ConnectedClientLimit)
                    {
                        Interlocked.Decrement(ref state.ActiveClients);
                        WriteDirect(client, "id=unknown\nstatus=BUSY\nerror=connected_client_limit");
                        client = null;
                        continue;
                    }
                    state.Clients.TryAdd(client, 0);
                    TcpClient accepted = client;
                    client = null;
                    if (!IsCurrentTransport(state) || !state.Clients.ContainsKey(accepted))
                    {
                        RemoveClient(state, accepted);
                        continue;
                    }
                    if (!ThreadPool.QueueUserWorkItem(_ => HandleClient(state, accepted)))
                    {
                        RemoveClient(state, accepted);
                        RequestIndicatorRefresh(state);
                    }
                    else
                    {
                        RequestIndicatorRefresh(state);
                    }
                }
                catch (SocketException) { if (!IsCurrentTransport(state)) return; }
                catch (ObjectDisposedException) { return; }
                catch { try { client?.Close(); } catch { } }
            }
        }

        private static void HandleClient(TransportState state, TcpClient client)
        {
            BridgeRequest request = null;
            try
            {
                if (!IsCurrentTransport(state)) return;
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
                    if (!IsCurrentTransport(state) || string.IsNullOrEmpty(state.Token) ||
                        string.IsNullOrEmpty(state.SessionId) || !TrySplitAuthentication(raw,
                            state.Token, out string payload))
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=authentication_failed");
                        return;
                    }
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                    if (!BridgeProtocol.TryParse(payload, state.SessionId, out request,
                        out BridgeResult parseFailure))
                    {
                        Decorate(parseFailure, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(parseFailure, "line"));
                        return;
                    }
                    request.TransportGeneration = state.Generation;
                    if (!IsCurrentTransport(state))
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=stale_transport");
                        return;
                    }
                    if (request.Command == "CANCEL")
                    {
                        BridgeResult cancelled = BridgeResult.Ok("core.cancel")
                            .Add("cancelled", Scheduler.Cancel(request.Argument, request.AgentId));
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
                    if (!IsCurrentTransport(state) || request.SessionId != SessionId)
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=stale_transport");
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
                        if (!IsCurrentTransport(state))
                        {
                            request.Cancelled = true;
                            if (!request.Started) Scheduler.Cancel(request.RequestId);
                        }
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
                RemoveClient(state, client);
                RequestIndicatorRefresh(state);
            }
        }

        private static void RequestIndicatorRefresh(TransportState state)
        {
            try
            {
                MainThread.Post(_ =>
                {
                    if (!shuttingDown && IsCurrentTransport(state))
                    {
                        MarkStateDirty();
                        PublishStateIfDirty();
                    }
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
                        : !IsCurrentRequestTransport(request)
                            ? BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport")
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
            if (!IsCurrentRequestTransport(request))
                return Decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    "core", BridgeProtocol.BridgeVersion);
            BridgeCommandDescriptor descriptor = request.PreparedDescriptor ?? BridgeDispatch.Describe(request);
            if (descriptor == null) return Decorate(BridgeResult.Fail(BridgeStatus.NOT_FOUND,
                "unknown_command"), request, "core", BridgeProtocol.BridgeVersion);
            if (request.SessionId != identity.SessionId)
                return Decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_session"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            BridgeResult mutationGate = ObserveMutationAuthorization(request, descriptor);
            if (mutationGate != null)
                return Decorate(mutationGate, request, descriptor.Provider, descriptor.ProviderVersion);
            BridgeResult authorization = Authorization.Authorize(request, descriptor, request.AuthToken,
                Settings.RemoteMutationEnabled);
            if (authorization != null)
                return Decorate(authorization, request, descriptor.Provider, descriptor.ProviderVersion);
            if (Authorization.TryGetCompleted(request, out BridgeResult cached))
                return Decorate(cached, request, descriptor.Provider, descriptor.ProviderVersion);
            if (!IsCurrentRequestTransport(request) || request.SessionId != SessionId)
                return Decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
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
            if (!IsCurrentRequestTransport(request) || request.SessionId != SessionId)
                return Decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            mutationGate = ObserveMutationAuthorization(request, descriptor);
            if (mutationGate != null)
                return Decorate(mutationGate, request, descriptor.Provider, descriptor.ProviderVersion);
            int tickBefore = BridgeGameState.TickManager?.TicksGame ?? -1;
            BridgeExecutionContext context = new BridgeExecutionContext(request, BridgeGameState.CurrentMap,
                () => request.Cancelled || request.ClientDisconnected);
            request.ExecutionReached = true;
            BridgeResult result = BridgeDispatch.Execute(context);
            bool staleTransport = !IsCurrentRequestTransport(request);
            if (request.Cancelled || request.ClientDisconnected || request.Expired ||
                request.SessionId != SessionId || staleTransport)
            {
                return Decorate(BridgeResult.Fail(request.Expired ? BridgeStatus.TIMEOUT :
                    (request.SessionId != SessionId || staleTransport) ? BridgeStatus.INCOMPATIBLE :
                        BridgeStatus.CANCELLED,
                    request.Expired ? "execution_deadline_expired" :
                        staleTransport ? "stale_transport" :
                        request.SessionId != SessionId ? "stale_session" : "execution_cancelled"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            }
            mutationGate = ObserveMutationAuthorization(request, descriptor);
            if (mutationGate != null)
                return Decorate(mutationGate, request, descriptor.Provider, descriptor.ProviderVersion);
            if (!IsCurrentRequestTransport(request) || request.SessionId != SessionId)
                return Decorate(BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "stale_transport"), request,
                    descriptor.Provider, descriptor.ProviderVersion);
            if (result == null && request.YieldExecution) return null;
            if (result == null) result = BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            result.TickBefore = tickBefore;
            result.TickAfter = BridgeGameState.TickManager?.TicksGame ?? -1;
            Decorate(result, request, descriptor.Provider, descriptor.ProviderVersion);
            return result;
        }

        private static BridgeResult ObserveMutationAuthorization(BridgeRequest request,
            BridgeCommandDescriptor descriptor)
        {
            if (descriptor == null || descriptor.Mode == BridgeCommandMode.PureRead) return null;
            request.MutationSettingEnabled = Settings.RemoteMutationEnabled;
            if (!request.MutationSettingEnabled)
            {
                request.MutationConfirmationState = "disabled";
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            }
            Game game = Current.Game;
            request.MutationGameLoaded = game != null;
            request.MutationGameIdentity = BridgeMutationConfirmation.IdentityFor(game) ?? "none";
            request.MutationSaveIdentity = BridgeMutationConfirmation.SaveIdentityFor(game) ?? "none";
            BridgeMutationConfirmationSnapshot snapshot = MutationConfirmation.Snapshot(
                request.SessionId, request.MutationSettingEnabled);
            request.MutationConfirmationState = snapshot.State;
            if (!request.MutationGameLoaded)
                return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "no_game_loaded");
            if (!MutationConfirmation.IsConfirmed(request.SessionId, request.MutationGameIdentity,
                request.MutationSaveIdentity))
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "in_game_confirmation_required",
                    "Confirm remote mutation in-game before executing a mutation.");
            return null;
        }

        private static void CompleteScheduled(BridgeRequest request, BridgeResult result)
        {
            BridgeCommandDescriptor descriptor = request.PreparedDescriptor ?? BridgeDispatch.Describe(request);
            if (descriptor == null) return;
            bool currentSession = string.Equals(request.SessionId, SessionId, StringComparison.Ordinal) &&
                IsCurrentRequestTransport(request);
            if (!request.IdempotentReplay && currentSession)
            {
                if (request.ExecutionReached) Authorization.Remember(request, result);
                Authorization.Audit(request, result);
            }
            BridgeMetrics.Record(descriptor, result, request.AgentId);
            if (currentSession)
                BridgeEventJournal.Record("command", request.Command + " status:" + result.Status +
                    " provider:" + descriptor.Provider + " agent:" + BridgeText.Clean(request.AgentId ?? "anonymous") +
                    " executionMs:" + result.ExecutionMs.ToString("0.###"));
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

        private static bool IsCurrentRequestTransport(BridgeRequest request)
        {
            if (request == null || request.TransportGeneration == 0) return true;
            TransportState current = Volatile.Read(ref transportState);
            return current != null && IsCurrentTransport(current) &&
                current.Generation == request.TransportGeneration;
        }

        private static bool IsCurrentTransport(TransportState state)
        {
            if (state == null || state.Invalidated || !active) return false;
            if (!ReferenceEquals(Volatile.Read(ref transportState), state)) return false;
            if (state.Generation != Volatile.Read(ref transportGeneration)) return false;
            SessionIdentity current = identity;
            if (string.IsNullOrEmpty(current.SessionId) || string.IsNullOrEmpty(current.Token) ||
                string.IsNullOrEmpty(state.SessionId) || string.IsNullOrEmpty(state.Token)) return false;
            return string.Equals(current.SessionId, state.SessionId, StringComparison.Ordinal) &&
                ConstantTimeTokenEquals(current.Token, state.Token);
        }

        private static bool TrySplitAuthentication(string raw, string expectedToken, out string payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(expectedToken)) return false;
            int separator = raw.IndexOf('|');
            if (separator <= 0) return false;
            string supplied = raw.Substring(0, separator);
            if (!ConstantTimeTokenEquals(supplied, expectedToken)) return false;
            payload = raw.Substring(separator + 1);
            return true;
        }

        private static bool ConstantTimeTokenEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            int difference = left.Length ^ right.Length;
            int length = Math.Max(left.Length, right.Length);
            for (int index = 0; index < length; index++)
            {
                int leftValue = index < left.Length ? left[index] : 0;
                int rightValue = index < right.Length ? right[index] : 0;
                difference |= leftValue ^ rightValue;
            }
            return difference == 0;
        }

        private static TransportState DetachTransportLocked()
        {
            TransportState stale = transportState;
            transportState = null;
            if (stale != null) stale.Invalidated = true;
            active = false;
            transportReady = false;
            activationIndexStarted = false;
            legacyInputPending = false;
            port = 0;
            Interlocked.Increment(ref transportGeneration);
            SessionIdentity current = identity;
            identity = new SessionIdentity(current.SessionId, string.Empty);
            MarkStateDirty();
            return stale;
        }

        private static void CloseTransportResources(TransportState stale)
        {
            if (stale == null) return;
            stale.Invalidated = true;
            try { stale.IdleTimer?.Dispose(); } catch { }
            try { stale.Listener?.Stop(); } catch { }
            foreach (TcpClient client in stale.Clients.Keys.ToArray())
            {
                try { client.Close(); } catch { }
            }
            stale.Clients.Clear();
            Volatile.Write(ref stale.ActiveClients, 0);
        }

        private static void RemoveClient(TransportState state, TcpClient client)
        {
            if (state == null || client == null) return;
            if (state.Clients.TryRemove(client, out _))
            {
                Interlocked.Decrement(ref state.ActiveClients);
                if (ReferenceEquals(Volatile.Read(ref transportState), state)) MarkStateDirty();
            }
            try { client.Close(); } catch { }
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

        private static bool WriteStatus(BridgeRuntimeStateSnapshot snapshot, string state, string extra = null)
        {
            AssertMainThread("status write");
            lock (StatusGate)
            {
                if (snapshot.Version != Interlocked.Read(ref stateVersion)) return false;
                long writeStart = Stopwatch.GetTimestamp();
                BridgeSessionContextSnapshot context = snapshot.Context;
                List<string> lines = new List<string>
                {
                    "bridge=" + state,
                    "name=RimWorld Dev Bridge",
                    "version=" + BridgeProtocol.BridgeVersion,
                    "protocol=" + BridgeProtocol.ProtocolVersion,
                    "schema=" + BridgeProtocol.CoreSchema,
                    "coreFingerprint=" + CoreFingerprint,
                    "processId=" + Process.GetCurrentProcess().Id,
                    "bootId=" + BootId,
                    "statusUtc=" + DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    "session=" + snapshot.Context.SessionId,
                    "transport=" + (snapshot.TransportActive ? "tcp+file" : "wake-file"),
                    "host=127.0.0.1",
                    "port=" + snapshot.Port,
                    "token=" + snapshot.TransportToken,
                    "clients=" + snapshot.ConnectedClients + "/" + snapshot.ConnectedClientLimit,
                    "context=" + context.WriteContext,
                    "writeContext=" + context.WriteContext,
                    "representativePlayerBehavior=" + context.RepresentativePlayerBehavior,
                    "writeLeaseActive=" + context.WriteLeaseActive,
                    "leaseState=" + context.LeaseState,
                    "leaseExpiresUtc=" + (context.LeaseExpiresUtc?.ToString("o",
                        System.Globalization.CultureInfo.InvariantCulture) ?? "none"),
                    "remoteMutationEnabled=" + snapshot.RemoteMutationEnabled,
                    "mutationConfirmation=" + snapshot.MutationConfirmation.State,
                    "mutationGameLoaded=" + snapshot.MutationConfirmation.GameLoaded,
                    "mutationConfirmed=" + snapshot.MutationConfirmation.Confirmed,
                    "transportGeneration=" + snapshot.TransportGeneration,
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
                if (!AtomicWrite(BridgePaths.StatusPath, string.Join("\n", lines))) return false;
                statusWriteMs = BridgeTiming.Milliseconds(writeStart);
                Interlocked.Increment(ref statusWriteCount);
                return true;
            }
        }

        private static bool AtomicWrite(string path, string content)
        {
            string temp = null;
            try
            {
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
                return true;
            }
            catch
            {
                try { if (!string.IsNullOrEmpty(temp) && File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void AssertMainThread(string operation)
        {
            MainThread.AssertOwnerThread(operation);
        }

        private static string ComputeCoreFingerprint()
        {
            try
            {
                string path = typeof(BridgeRuntime).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    using (SHA256 algorithm = SHA256.Create())
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                        return string.Concat(algorithm.ComputeHash(stream).Select(value => value.ToString("X2")));
                }
                return typeof(BridgeRuntime).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            }
            catch
            {
                return "unknown";
            }
        }

        internal sealed class BridgeRuntimeStateSnapshot
        {
            internal readonly long Version;
            internal readonly bool TransportActive;
            internal readonly bool TransportReady;
            internal readonly int TransportGeneration;
            internal readonly int ConnectedClients;
            internal readonly int ConnectedClientLimit;
            internal readonly int Port;
            internal readonly string TransportToken;
            internal readonly BridgeSessionContextSnapshot Context;
            internal readonly bool RemoteMutationEnabled;
            internal readonly BridgeMutationConfirmationSnapshot MutationConfirmation;

            internal BridgeRuntimeStateSnapshot(long version, bool transportActive, bool transportReady,
                int transportGeneration, int connectedClients, int connectedClientLimit, int port,
                string transportToken, BridgeSessionContextSnapshot context, bool remoteMutationEnabled,
                BridgeMutationConfirmationSnapshot mutationConfirmation)
            {
                Version = version;
                TransportActive = transportActive;
                TransportReady = transportReady;
                TransportGeneration = transportGeneration;
                ConnectedClients = connectedClients;
                ConnectedClientLimit = connectedClientLimit;
                Port = port;
                TransportToken = transportToken ?? string.Empty;
                Context = context;
                RemoteMutationEnabled = remoteMutationEnabled;
                MutationConfirmation = mutationConfirmation;
            }
        }

        private sealed class TransportState
        {
            internal readonly int Generation;
            internal readonly TcpListener Listener;
            internal readonly string SessionId;
            internal readonly string Token;
            internal readonly ConcurrentDictionary<TcpClient, byte> Clients =
                new ConcurrentDictionary<TcpClient, byte>();
            internal volatile Timer IdleTimer;
            internal volatile bool Invalidated;
            internal int ActiveClients;

            internal TransportState(int generation, TcpListener listener, string sessionId, string token)
            {
                Generation = generation;
                Listener = listener;
                SessionId = sessionId;
                Token = token;
            }
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

        internal static void Record(BridgeCommandDescriptor descriptor, BridgeResult result, string agentId = null)
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
                if (metric.Agents.Count < 64 && !string.IsNullOrWhiteSpace(agentId)) metric.Agents.Add(agentId);
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
                         " agents:" + pair.Value.Agents.Count +
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
            internal readonly HashSet<string> Agents = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
