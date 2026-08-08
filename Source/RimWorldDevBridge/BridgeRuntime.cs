using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldDevBridge
{
    internal static class BridgeRuntime
    {
        private const int IdleSeconds = 180;
        private static readonly object Gate = new object();
        private static readonly BridgeAuthorization Authorization = new BridgeAuthorization();
        private static readonly BridgeIdentityAuthority IdentityAuthority = new BridgeIdentityAuthority();
        private static readonly BridgeMutationConfirmation MutationConfirmation =
            new BridgeMutationConfirmation();
        private static BridgeSharedOperationCoordinator SharedOperations;
        private static BridgeRuntimeSlotManager RuntimeSlots;
        private static BridgeDeploymentManager DeploymentManager;
        private static readonly BridgeScheduledRequestExecutor RequestExecutor =
            new BridgeScheduledRequestExecutor(Authorization, () => SessionId,
                () => Settings.RemoteMutationEnabled, IsCurrentRequestTransport,
                ObserveMutationAuthorization, Decorate);
        private static readonly BridgeScheduler Scheduler =
            new BridgeScheduler(RequestExecutor.Execute, CompleteScheduledRequest);
        private static readonly string BootId = Guid.NewGuid().ToString("N");
        private static readonly BridgeMainThreadContext MainThread = new BridgeMainThreadContext();
        private static readonly BridgeRequestPreparation RequestPreparation =
            new BridgeRequestPreparation(MainThread, () => SessionId, IsCurrentRequestTransport,
                CoordinateRequest, CompleteCoordinatedRequest);
        private static readonly BridgeLegacyFileProtocol LegacyFileProtocol =
            new BridgeLegacyFileProtocol(() => AssertMainThread("legacy file processing"),
                () => SessionId, RequestPreparation, Scheduler, Decorate, CompleteCoordinatedRequest);
        private static readonly Harmony Harmony = new Harmony("lan.rimworld.devbridge.v2");
        private static readonly BridgeFileActivation FileActivation = new BridgeFileActivation(
            () => shuttingDown, () => active, () => AssertMainThread("file activation"), StartTransport,
            () => BridgeFileOperations.TryDelete(BridgePaths.WakePath));
        private static BridgeTransportState transportState;
        private static volatile bool active;
        private static volatile bool transportReady;
        private static volatile bool activationIndexStarted;
        private static volatile bool shuttingDown;
        private static bool updatePatched;
        private static int transportGeneration;
        private static long gameTransitionSequence;
        private static long finalizedGameTransitionSequence = long.MinValue;
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
        private static long bootstrapManagedDeltaBytes;
        private static long leaseExpiryTicks;
        private static string coreFingerprint;
        private static string loadedAssemblyFingerprint;
        private static string activeRuntimeSlotId;
        private static bool bootstrapped;
        private static readonly BridgeLifecycleDispatch LifecycleDispatch =
            new BridgeLifecycleDispatch(MainThread, () => shuttingDown, CompleteFinalizeInit);
        private static readonly BridgeLeaseExpiryScheduler LeaseExpiry =
            new BridgeLeaseExpiryScheduler(MainThread, () => shuttingDown, CaptureStateSnapshot,
                MarkStateDirty, () => PublishStateIfDirty(true),
                value => Interlocked.Exchange(ref leaseExpiryTicks, value));

        internal static bool Active => active;
        internal static int ActiveClients
        {
            get
            {
                BridgeTransportState current = Volatile.Read(ref transportState);
                return current == null ? 0 : Math.Max(0, Volatile.Read(ref current.ActiveClients));
            }
        }
        internal static int ConnectedClientLimit => Settings.ConnectedClientLimit;
        internal static double BootstrapMs => bootstrapMs;
        internal static double HarmonyMs => harmonyMs;
        internal static double FinalizeInitMs => finalizeInitMs;
        internal static double ActivationMs => activationMs;
        internal static long BootstrapManagedDeltaBytes => bootstrapManagedDeltaBytes;
        internal static string SessionId => identity.SessionId;
        internal static string BootIdForClients => BootId;
        internal static int ProcessIdForClients => Process.GetCurrentProcess().Id;
        internal static string CoreFingerprint => coreFingerprint ?? (coreFingerprint = ComputeCoreFingerprint());
        internal static string ActiveRuntimeSlotId => activeRuntimeSlotId;
        internal static string LoadedAssemblyFingerprint => loadedAssemblyFingerprint ??
            (loadedAssemblyFingerprint = ComputeLoadedAssemblyFingerprint());
        internal static string ArtifactFingerprint
        {
            get
            {
                if (DeploymentManager == null || string.IsNullOrWhiteSpace(activeRuntimeSlotId)) return "none";
                try { return DeploymentManager.ReadCurrent(activeRuntimeSlotId)?.ArtifactFingerprint ?? "none"; }
                catch { return "none"; }
            }
        }
        internal static BridgeRuntimeStateSnapshot StateSnapshot => CaptureStateSnapshot();
        internal static BridgeSessionContextSnapshot SessionContext => StateSnapshot.Context;
        internal static int StatusWriteCountForTests => BridgeStatusPublisher.WriteCountForTests;
        internal static int IndicatorRefreshCountForTests => BridgeIndicator.RefreshCountForTests;
        internal static long StateVersionForTests => Interlocked.Read(ref stateVersion);
        internal static void ResetStatePublicationCountersForTests()
        {
            BridgeStatusPublisher.ResetWriteCountForTests();
            BridgeIndicator.ResetRefreshCountForTests();
        }
        internal static string WriteContext => SessionContext.WriteContext;
        internal static int EffectiveQueueCapacity => Scheduler.QueueCapacity;
        internal static int EffectiveMainThreadBudgetMs => Scheduler.MainThreadBudgetMs;
        internal static bool QueueCapacityPending => Settings.QueueCapacity != EffectiveQueueCapacity;
        internal static bool MainThreadBudgetPending => Settings.MainThreadBudgetMs != EffectiveMainThreadBudgetMs;
        internal static bool SchedulerSettingsPending => QueueCapacityPending || MainThreadBudgetPending;
        internal static long LifecycleSequenceForTests => Interlocked.Read(ref gameTransitionSequence);
        internal static long FinalizedLifecycleSequenceForTests =>
            Interlocked.Read(ref finalizedGameTransitionSequence);
        internal static int FinalizeInitRequestThreadIdForTests =>
            LifecycleDispatch.FinalizeInitRequestThreadId;
        internal static int FinalizeInitExecutionThreadIdForTests =>
            LifecycleDispatch.FinalizeInitExecutionThreadId;
        internal static int FinalizeInitDeferredCountForTests =>
            LifecycleDispatch.FinalizeInitDeferredCount;
        internal static int PendingLifecycleWorkForTests => LifecycleDispatch.PendingCount;
        internal static long CoalescedLifecycleWorkForTests => LifecycleDispatch.CoalescedCount;
        internal static long DroppedStaleLifecycleWorkForTests => LifecycleDispatch.DroppedStaleCount;
        internal static long PublishedLifecycleSequenceForTests =>
            Interlocked.Read(ref publishedGameTransitionSequence);
        internal static int PublishedLifecycleThreadIdForTests =>
            Volatile.Read(ref publishedGameTransitionThreadId);
        internal static int TransportGenerationForTests => Volatile.Read(ref transportGeneration);
        internal static int TransportResourceGenerationForTests
        {
            get
            {
                BridgeTransportState current = Volatile.Read(ref transportState);
                return current == null ? 0 : current.Generation;
            }
        }
        internal static int TransportPortForTests => Volatile.Read(ref port);
        internal static void SignalWakeForTests() => FileActivation.SignalWake();
        internal static void CaptureStatusPathForTests() => statusPath = BridgePaths.StatusPath;
        internal static void BindCurrentGameForTests(Game game)
        {
            AssertMainThread("test game confirmation binding");
            MutationConfirmation.BindCurrentGame(SessionId, game);
            MarkStateDirty();
            PublishStateIfDirty(true);
        }
        internal static bool AuthenticateForTests(string raw, string expectedToken, out string payload) =>
            BridgeTransportAuthentication.TrySplit(raw, expectedToken, out payload);

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
                BridgeTransportState current = Volatile.Read(ref transportState);
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
                     Interlocked.Read(ref gameTransitionSequence),
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
            if (!BridgeStatusPublisher.Write(new BridgeStatusPublication(snapshot, state, extra,
                bootstrapMs, harmonyMs, finalizeInitMs, activationMs, bootstrapManagedDeltaBytes),
                CurrentStateVersion)) Interlocked.Exchange(ref stateDirty, 1);
        }

        private static long CurrentStateVersion() => Interlocked.Read(ref stateVersion);

        private static BridgeResult CoordinateRequest(BridgeRequest request)
        {
            if (SharedOperations == null || string.IsNullOrWhiteSpace(request.OperationKind)) return null;
            if (!Enum.TryParse(request.OperationKind, true, out BridgeOperationKind operationKind))
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_operation_kind");
            if (!TryParseDesiredState(request.DesiredState, out BridgeDesiredState desiredState))
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_desired_state");

            BridgeClientIdentity identity = null;
            BridgeOperationJoinResult joined = null;
            BridgeRuntimeSlotAllocation allocatedSlot = null;
            try
            {
                BridgeOperationCompatibilityKey compatibility = BuildCompatibility(request, operationKind,
                    desiredState);
                identity = BridgeClientIdentity.Create(request.AgentId,
                    request.ClientInstanceId, request.ConnectionSessionId, request.CorrelationId,
                    request.ParticipantId);
                // Persist normalized defaults on the request. Completion, cancellation, and
                // child work must reconstruct the same caller identity rather than minting
                // a different anonymous identity on each path.
                request.AgentId = identity.AgentId;
                request.ClientInstanceId = identity.ClientInstanceId;
                request.ConnectionSessionId = identity.ConnectionSessionId;
                request.CorrelationId = identity.RequestCorrelationId;
                request.ParticipantId = identity.ParticipantId;
                if (!AuthenticateIdentity(request, identity))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "client_identity_authentication_failed");
                joined = SharedOperations.Join(new BridgeOperationJoinRequest
                {
                    Identity = identity,
                    OperationId = request.OperationId,
                    OperationKind = operationKind,
                    DesiredState = desiredState,
                    Compatibility = compatibility,
                    RuntimeSlotId = request.RuntimeSlotId,
                    DeploymentId = request.DeploymentId,
                    ArtifactFingerprint = request.ArtifactFingerprint,
                    LoadedAssemblyFingerprint = request.ExpectedLoadedAssemblyFingerprint,
                    KeepRunning = request.KeepRunning
                });
                if (joined.Operation == null)
                    return BridgeResult.Fail(BridgeStatus.BUSY, "operation_capacity", joined.NextAction)
                        .Add("capacityState", joined.CapacityState ?? "queued");

                request.OperationId = joined.Operation.OperationId;
                request.ParticipantId = joined.ParticipantId;
                request.CompatibilityKey = joined.Operation.CompatibilityKey;
                if (!joined.Created)
                {
                    if (joined.Operation.OperationState == BridgeOperationState.Queued && RuntimeSlots != null)
                    {
                        PromoteQueuedCapacity();
                        BridgeRuntimeSlotDefinition queuedSlot = RuntimeSlots.FindLease(joined.Operation.OperationId,
                            identity.AgentId, identity.ClientInstanceId);
                        if (queuedSlot == null)
                        {
                            BridgeRuntimeSlotAllocation retryAllocation = RuntimeSlots.Allocate(new BridgeRuntimeSlotRequest
                            {
                                Compatibility = compatibility,
                                RequestedRuntimeSlotId = request.RuntimeSlotId,
                                AgentId = identity.AgentId,
                                ClientInstanceId = identity.ClientInstanceId,
                                OperationId = joined.Operation.OperationId,
                                RequiresNewProcess = request.RequiresProcessReplacement
                            });
                            queuedSlot = retryAllocation?.Slot;
                        }
                        if (queuedSlot != null)
                        {
                            activeRuntimeSlotId = queuedSlot.RuntimeSlotId;
                            joined.Operation = SharedOperations.BindRuntimeSlot(joined.Operation.OperationId,
                                identity, queuedSlot.RuntimeSlotId, request.DeploymentId,
                                request.ArtifactFingerprint);
                            request.SharedOperationRegistered = true;
                            ApplyOperationFields(request, joined.Operation, null);
                            return null;
                        }
                    }
                    request.SharedOperationRegistered = false;
                    return OperationResult(request, joined.Operation,
                        joined.Operation.OperationState == BridgeOperationState.Queued ? BridgeStatus.BUSY : BridgeStatus.PARTIAL,
                        joined.Operation.OperationState == BridgeOperationState.Queued ? "runtime_slot_capacity" : "operation_joined",
                        joined.NextAction ?? "observe the shared operation");
                }
                BridgeRuntimeSlotAllocation allocation = RuntimeSlots?.Allocate(new BridgeRuntimeSlotRequest
                {
                    Compatibility = compatibility,
                    RequestedRuntimeSlotId = request.RuntimeSlotId,
                    AgentId = identity.AgentId,
                    ClientInstanceId = identity.ClientInstanceId,
                    OperationId = joined.Operation.OperationId,
                    RequiresNewProcess = request.RequiresProcessReplacement
                });
                allocatedSlot = allocation;
                if (allocation != null && !allocation.Allocated)
                {
                    request.SharedOperationRegistered = false;
                    BridgeOperationRecord queued = SharedOperations.SetCapacity(joined.Operation.OperationId,
                        identity, allocation.CapacityState, allocation.NextAction, true);
                    return OperationResult(request, queued, BridgeStatus.BUSY, "runtime_slot_capacity",
                        allocation.NextAction);
                }
                if (allocation?.Slot != null)
                {
                    if (!RegisterCurrentManagedProcess(request, allocation.Slot, identity))
                    {
                        RuntimeSlots.Release(allocation.Slot.RuntimeSlotId, joined.Operation.OperationId,
                            identity.AgentId, identity.ClientInstanceId, false);
                        AbortCoordinatedOperation(request, identity, joined,
                            BridgeResult.Fail(BridgeStatus.FORBIDDEN, "managed_process_identity_required"));
                        return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "managed_process_identity_required");
                    }
                    BridgeResult deploymentFailure = VerifyDeploymentForRequest(request,
                        allocation.Slot.RuntimeSlotId);
                    if (deploymentFailure != null)
                    {
                        RuntimeSlots.Release(allocation.Slot.RuntimeSlotId, joined.Operation.OperationId,
                            identity.AgentId, identity.ClientInstanceId, false);
                        AbortCoordinatedOperation(request, identity, joined, deploymentFailure);
                        return deploymentFailure;
                    }
                    activeRuntimeSlotId = allocation.Slot.RuntimeSlotId;
                    joined.Operation = SharedOperations.BindRuntimeSlot(joined.Operation.OperationId, identity,
                        allocation.Slot.RuntimeSlotId, request.DeploymentId, request.ArtifactFingerprint);
                }
                request.SharedOperationRegistered = true;
                ApplyOperationFields(request, joined.Operation, null);
                return null;
            }
            catch (ArgumentException exception)
            {
                ReleaseFailedAllocation(allocatedSlot, joined, identity);
                BridgeResult failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_operation_request",
                    BridgeText.Clean(exception.GetBaseException().Message));
                AbortCoordinatedOperation(request, identity, joined, failure);
                return failure;
            }
            catch (Exception exception)
            {
                ReleaseFailedAllocation(allocatedSlot, joined, identity);
                BridgeResult failure = BridgeResult.Fail(BridgeStatus.ERROR, "operation_coordination_failed",
                    BridgeText.Clean(exception.GetBaseException().Message));
                AbortCoordinatedOperation(request, identity, joined, failure);
                return failure;
            }
        }

        internal static BridgeResult CoordinateRequestForTests(BridgeRequest request,
            BridgeSharedOperationCoordinator sharedOperations, BridgeRuntimeSlotManager runtimeSlots)
        {
            BridgeSharedOperationCoordinator previousOperations = SharedOperations;
            BridgeRuntimeSlotManager previousSlots = RuntimeSlots;
            BridgeDeploymentManager previousDeployment = DeploymentManager;
            SharedOperations = sharedOperations;
            RuntimeSlots = runtimeSlots;
            DeploymentManager = null;
            try { return CoordinateRequest(request); }
            finally
            {
                SharedOperations = previousOperations;
                RuntimeSlots = previousSlots;
                DeploymentManager = previousDeployment;
            }
        }

        private static void ReleaseFailedAllocation(BridgeRuntimeSlotAllocation allocation,
            BridgeOperationJoinResult joined, BridgeClientIdentity identity)
        {
            if (allocation?.Slot == null || joined == null || identity == null || RuntimeSlots == null) return;
            try
            {
                RuntimeSlots.Release(allocation.Slot.RuntimeSlotId, joined.OperationId,
                    identity.AgentId, identity.ClientInstanceId, false);
            }
            catch { }
        }

        private static bool AuthenticateIdentity(BridgeRequest request, BridgeClientIdentity identity)
        {
            // Legacy callers remain transport-authenticated. New clients bind the stable
            // client label to a separate credential before it can own a lease or operation.
            if (string.IsNullOrWhiteSpace(request.ClientCredential)) return true;
            BridgeClientRegistration registration;
            if (IdentityAuthority.Authenticate(identity, request.ClientCredential, out registration)) return true;
            try
            {
                IdentityAuthority.Register(identity.AgentId, identity.ClientInstanceId,
                    request.ClientCredential);
                return IdentityAuthority.Authenticate(identity, request.ClientCredential, out registration);
            }
            catch (InvalidOperationException) { return false; }
        }

        private static bool AuthenticateClient(string agentId, string clientInstanceId, string credential)
        {
            if (string.IsNullOrWhiteSpace(clientInstanceId)) return true;
            if (string.Equals(clientInstanceId, "client-legacy", StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(agentId) || string.Equals(agentId, "anonymous",
                    StringComparison.Ordinal))) return true;
            if (string.IsNullOrWhiteSpace(credential)) return false;
            BridgeClientIdentity identity = BridgeClientIdentity.Create(agentId, clientInstanceId,
                "lease-connection", "lease-correlation", "lease-participant");
            BridgeClientRegistration registration;
            if (IdentityAuthority.Authenticate(identity, credential, out registration)) return true;
            try
            {
                IdentityAuthority.Register(identity.AgentId, identity.ClientInstanceId, credential);
                return IdentityAuthority.Authenticate(identity, credential, out registration);
            }
            catch (InvalidOperationException) { return false; }
        }

        private static BridgeResult VerifyDeploymentForRequest(BridgeRequest request, string runtimeSlotId)
        {
            if (DeploymentManager == null || string.IsNullOrWhiteSpace(request.DeploymentId)) return null;
            if (string.IsNullOrWhiteSpace(runtimeSlotId))
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "deployment_slot_required");
            try
            {
                BridgeDeploymentManifest manifest = DeploymentManager.ReadCurrent(runtimeSlotId);
                if (manifest == null || !string.Equals(manifest.DeploymentId, request.DeploymentId,
                        StringComparison.Ordinal) ||
                    (!string.IsNullOrWhiteSpace(request.ArtifactFingerprint) &&
                     !string.Equals(manifest.ArtifactFingerprint, request.ArtifactFingerprint,
                         StringComparison.OrdinalIgnoreCase)))
                    return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "deployed_fingerprint_mismatch");
                if (!string.IsNullOrWhiteSpace(request.ExpectedLoadedAssemblyFingerprint) &&
                    !DeploymentManager.VerifyLoaded(manifest, request.ExpectedLoadedAssemblyFingerprint))
                    return BridgeResult.Fail(BridgeStatus.INCOMPATIBLE, "loaded_assembly_fingerprint_mismatch");
                return null;
            }
            catch (Exception exception)
            {
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "deployment_verification_failed",
                    BridgeText.Clean(exception.GetBaseException().Message));
            }
        }

        private static bool RegisterCurrentManagedProcess(BridgeRequest request,
            BridgeRuntimeSlotDefinition slot, BridgeClientIdentity identity)
        {
            if (request == null || slot == null || identity == null || RuntimeSlots == null) return false;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    string start = process.StartTime.ToUniversalTime().Ticks.ToString(
                        CultureInfo.InvariantCulture);
                    long lifecycle = 0;
                    long.TryParse(slot.LifecycleGeneration, out lifecycle);
                    if (request.ExpectedProcessId > 0 && request.ExpectedProcessId != process.Id ||
                        !string.IsNullOrWhiteSpace(request.ExpectedProcessStartIdentity) &&
                        !string.Equals(request.ExpectedProcessStartIdentity, start, StringComparison.Ordinal) ||
                        !string.IsNullOrWhiteSpace(request.ExpectedProcessSessionId) &&
                        !string.Equals(request.ExpectedProcessSessionId, SessionId, StringComparison.Ordinal) ||
                        request.ExpectedProcessLifecycleGeneration > 0 &&
                        request.ExpectedProcessLifecycleGeneration != lifecycle) return false;
                    BridgeProcessIdentity current = new BridgeProcessIdentity
                    {
                        Pid = process.Id,
                        ProcessStartIdentity = start,
                        SessionId = SessionId,
                        LifecycleGeneration = lifecycle,
                        RuntimeSlotId = slot.RuntimeSlotId,
                        LoadedAssemblyFingerprint = LoadedAssemblyFingerprint,
                        ExecutablePath = process.MainModule == null ? string.Empty : process.MainModule.FileName,
                        ProfileFingerprint = BridgeHashing.Sha256(slot.ManagedProfile ?? string.Empty)
                    };
                    return RuntimeSlots.ClaimManagedProcess(slot.RuntimeSlotId, current,
                        request.OperationId, identity.AgentId, identity.ClientInstanceId);
                }
            }
            catch { return false; }
        }

        private static void AbortCoordinatedOperation(BridgeRequest request, BridgeClientIdentity identity,
            BridgeOperationJoinResult joined, BridgeResult failure)
        {
            if (joined == null || !joined.Created || identity == null || SharedOperations == null ||
                request.SharedOperationRegistered || failure == null) return;
            try
            {
                SharedOperations.Complete(joined.OperationId, identity, null,
                    failure.Status.ToString().ToLowerInvariant());
            }
            catch { }
        }

        private static bool TryParseDesiredState(string value, out BridgeDesiredState desiredState)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "bridge" : value.Trim();
            normalized = normalized.Replace("_", string.Empty).Replace("-", string.Empty);
            if (string.Equals(normalized, "testready", StringComparison.OrdinalIgnoreCase))
            {
                desiredState = BridgeDesiredState.TestReady;
                return true;
            }
            return Enum.TryParse(normalized, true, out desiredState);
        }

        private static BridgeOperationCompatibilityKey BuildCompatibility(BridgeRequest request,
            BridgeOperationKind operationKind, BridgeDesiredState desiredState)
        {
            BridgeOperationCompatibilityKey calculated = BridgeOperationCompatibilityKey.Create(operationKind,
                desiredState, request.ManagedProfile ?? "attached",
                request.RimWorldVersion ?? VersionControl.CurrentVersionStringWithoutBuild,
                request.ModSetFingerprint, request.ModLoadOrderFingerprint,
                request.SourceBuildIdentity ?? CoreFingerprint, request.RuntimeSlotId,
                 request.ExpectedCoreFingerprint ?? CoreFingerprint, request.ExpectedAdapterFingerprint,
                 request.ConfigurationFingerprint,
                 request.UserRootFingerprint ?? BridgeHashing.StablePathFingerprint(new[] { BridgePaths.UserRoot }),
                 request.SaveTarget, request.MapTarget, request.RequiresProcessReplacement,
                 request.LifecycleGeneration, request.MutationScope ?? request.Command,
                 request.DeploymentId, request.ArtifactFingerprint,
                 request.ExpectedLoadedAssemblyFingerprint);
            if (!string.IsNullOrWhiteSpace(request.CompatibilityKey) &&
                !string.Equals(calculated.ToString(),
                    BridgeOperationCompatibilityKey.FromDigest(request.CompatibilityKey).ToString(),
                    StringComparison.Ordinal))
                throw new ArgumentException("compatibility_key_mismatch");
            return calculated;
        }

        private static void CompleteScheduledRequest(BridgeRequest request, BridgeResult result)
        {
            RequestExecutor.Complete(request, result);
            CompleteCoordinatedRequest(request, result);
        }

        private static void CompleteCoordinatedRequest(BridgeRequest request, BridgeResult result)
        {
            if (!request.SharedOperationRegistered || SharedOperations == null || result == null) return;
            if (Interlocked.Exchange(ref request.SharedOperationCompletionClaimed, 1) != 0) return;
            request.SharedOperationRegistered = false;
            BridgeOperationRecord operation = null;
            BridgeClientIdentity identity = BridgeClientIdentity.Create(request.AgentId,
                request.ClientInstanceId, request.ConnectionSessionId, request.CorrelationId,
                request.ParticipantId);
            try
            {
                operation = SharedOperations.Complete(request.OperationId, identity,
                    result.LoadedAssemblyFingerprint,
                    result.IsSuccess ? null : result.Status.ToString().ToLowerInvariant());
                ApplyOperationFields(request, operation, result);
            }
            catch (Exception exception)
            {
                result.Status = BridgeStatus.ERROR;
                result.Add("error", "operation_completion_failed");
                result.Warn(BridgeText.Clean(exception.GetBaseException().Message));
            }
            finally
            {
                string runtimeSlotId = operation?.RuntimeSlotId ?? request.RuntimeSlotId;
                string operationId = operation?.OperationId ?? request.OperationId;
                bool keepRunning = operation != null && operation.KeepRunning;
                if (RuntimeSlots != null && !string.IsNullOrWhiteSpace(runtimeSlotId))
                    RuntimeSlots.Release(runtimeSlotId, operationId, identity.AgentId,
                        identity.ClientInstanceId, keepRunning);
                if (!keepRunning) activeRuntimeSlotId = null;
                if (!keepRunning) PromoteQueuedCapacity();
            }
        }

        private static void PromoteQueuedCapacity()
        {
            if (RuntimeSlots == null || SharedOperations == null) return;
            for (int attempt = 0; attempt < 64; attempt++)
            {
                BridgeRuntimeSlotAllocation allocation = RuntimeSlots.TryAllocateNext();
                if (allocation == null || !allocation.Allocated || allocation.Slot == null) return;
                BridgeRuntimeSlotLease lease = allocation.Slot.ActiveLeases.LastOrDefault();
                if (lease == null) return;
                BridgeClientIdentity identity;
                try { identity = SharedOperations.ParticipantIdentity(lease.OperationId); }
                catch { identity = null; }
                if (identity == null)
                {
                    RuntimeSlots.Release(allocation.Slot.RuntimeSlotId, lease.OperationId, lease.AgentId,
                        lease.ClientInstanceId, false);
                    continue;
                }
                try
                {
                    BridgeOperationRecord operation = SharedOperations.Snapshot().FirstOrDefault(item =>
                        string.Equals(item.OperationId, lease.OperationId, StringComparison.Ordinal));
                    if (operation == null || operation.Terminal)
                    {
                        RuntimeSlots.Release(allocation.Slot.RuntimeSlotId, lease.OperationId,
                            lease.AgentId, lease.ClientInstanceId, false);
                        continue;
                    }
                    SharedOperations.BindRuntimeSlot(lease.OperationId, identity,
                        allocation.Slot.RuntimeSlotId, operation.DeploymentId, operation.ArtifactFingerprint);
                    activeRuntimeSlotId = allocation.Slot.RuntimeSlotId;
                }
                catch
                {
                    RuntimeSlots.Release(allocation.Slot.RuntimeSlotId, lease.OperationId, lease.AgentId,
                        lease.ClientInstanceId, false);
                }
                return;
            }
        }

        private static BridgeResult OperationResult(BridgeRequest request, BridgeOperationRecord operation,
            BridgeStatus status, string code, string nextAction)
        {
            BridgeResult result = BridgeResult.Fail(status, code);
            if (!string.IsNullOrWhiteSpace(nextAction)) result.Add("nextAction", nextAction);
            ApplyOperationFields(request, operation, result);
            return result;
        }

        private static void ApplyOperationFields(BridgeRequest request, BridgeOperationRecord operation,
            BridgeResult result)
        {
            if (operation == null) return;
            request.OperationId = operation.OperationId;
            request.CompatibilityKey = operation.CompatibilityKey;
            request.RuntimeSlotId = operation.RuntimeSlotId;
            request.DeploymentId = operation.DeploymentId;
            request.ArtifactFingerprint = operation.ArtifactFingerprint;
            if (result == null) return;
            result.OperationId = operation.OperationId;
            result.OperationKind = operation.OperationKind.ToString();
            result.OperationState = operation.OperationState.ToString();
            result.CompatibilityKey = operation.CompatibilityKey;
            result.DesiredState = operation.DesiredState.ToString();
            result.RuntimeSlotId = operation.RuntimeSlotId;
            result.DeploymentId = operation.DeploymentId;
            result.ArtifactFingerprint = operation.ArtifactFingerprint;
            result.LoadedAssemblyFingerprint = operation.LoadedAssemblyFingerprint;
            result.ProcessId = operation.Pid;
            result.ProcessStartIdentity = operation.ProcessStartIdentity;
            result.SessionId = operation.SessionId ?? result.SessionId;
            result.LifecycleGeneration = operation.LifecycleGeneration;
            result.ProgressSequence = operation.ProgressSequence;
            result.LastProgressAtUtc = operation.LastProgressAtUtc;
            result.Terminal = operation.Terminal;
            result.Recoverable = operation.Recoverable;
            result.RetrySafe = operation.RetrySafe;
            result.NextAction = operation.NextAction;
            result.CapacityState = operation.CapacityState;
            result.KeepRunning = operation.KeepRunning;
        }

        internal static void Bootstrap(string modRoot, long constructionStart, long managedBefore)
        {
            AssertMainThread("bootstrap");
            if (bootstrapped) return;
            bootstrapped = true;
            BridgePaths.Initialize(modRoot);
            statusPath = BridgePaths.StatusPath;
            string coordinationRoot = Path.Combine(BridgePaths.UserRoot, "Coordination");
            Directory.CreateDirectory(coordinationRoot);
            SharedOperations = new BridgeSharedOperationCoordinator(
                statePath: Path.Combine(coordinationRoot, "operations.json"));
            RuntimeSlots = new BridgeRuntimeSlotManager(2, root: Path.Combine(coordinationRoot, "slots"));
            DeploymentManager = new BridgeDeploymentManager(Path.Combine(coordinationRoot, "deployments"));
            IdentityAuthority.Load(Path.Combine(coordinationRoot, "identities.json"));
            SharedOperations.RecoverAfterCoordinatorRestart();
            RestoreActiveRuntimeSlot();
            Authorization.RotateSession(identity.SessionId);
            Scheduler.Configure(MainThread, identity.SessionId, Settings.QueueCapacity, Settings.MainThreadBudgetMs);
            long harmonyStart = Stopwatch.GetTimestamp();
            Harmony.Patch(AccessTools.PropertySetter(typeof(Current), nameof(Current.Game)),
                prefix: new HarmonyMethod(typeof(BridgeRuntime), nameof(OnGameChanging)));
            EnsureUpdatePatch();
            harmonyMs = BridgeTiming.Milliseconds(harmonyStart);
            UnityEngine.Application.quitting += Shutdown;
            FileActivation.Initialize();
            bootstrapMs = BridgeTiming.Milliseconds(constructionStart);
            MarkStateDirty();
            PublishStateIfDirty(true);
            bootstrapMs = BridgeTiming.Milliseconds(constructionStart);
            bootstrapManagedDeltaBytes = GC.GetTotalMemory(false) - managedBefore;
        }

        private static void RestoreActiveRuntimeSlot()
        {
            if (SharedOperations == null || RuntimeSlots == null) return;
            HashSet<string> validSlots = new HashSet<string>(RuntimeSlots.Snapshot()
                .Where(item => item.ManagedOwnership && item.ActiveProcess && item.Process != null &&
                    item.Process.Pid == Process.GetCurrentProcess().Id)
                .Select(item => item.RuntimeSlotId), StringComparer.Ordinal);
            BridgeOperationRecord active = SharedOperations.Snapshot().Where(item => !item.Terminal &&
                !string.IsNullOrWhiteSpace(item.RuntimeSlotId) && validSlots.Contains(item.RuntimeSlotId))
                .OrderByDescending(item => item.LastProgressAtUtc).FirstOrDefault();
            activeRuntimeSlotId = active == null ? null : active.RuntimeSlotId;
        }

        public static void OnFinalizeInit()
        {
            LifecycleDispatch.OnFinalizeInit(Interlocked.Read(ref gameTransitionSequence));
        }

        private static void CompleteFinalizeInit(long sequence)
        {
            if (shuttingDown || sequence != Interlocked.Read(ref gameTransitionSequence) ||
                sequence == Interlocked.Read(ref finalizedGameTransitionSequence)) return;
            if (!MainThread.IsOwnerEstablished || !MainThread.IsOwnerThread)
            {
                LifecycleDispatch.PostFinalizeInit(sequence);
                return;
            }
            LifecycleDispatch.RecordExecutionThread();
            AssertMainThread("finalize initialization");
            long start = Stopwatch.GetTimestamp();
            RotateSession("game");
            if (shuttingDown || sequence != Interlocked.Read(ref gameTransitionSequence)) return;
            MutationConfirmation.BindCurrentGame(SessionId, Current.Game);
            if (shuttingDown || sequence != Interlocked.Read(ref gameTransitionSequence)) return;
            bool wakePending = File.Exists(BridgePaths.WakePath);
            if (wakePending)
            {
                BridgeFileOperations.TryDelete(BridgePaths.WakePath);
                StartTransport();
            }
            if (File.Exists(BridgePaths.InputPath)) FileActivation.SignalInput();
            BridgeEventJournal.Record("lifecycle", "game finalized session:" + identity.SessionId);
            finalizeInitMs = BridgeTiming.Milliseconds(start);
            Interlocked.Exchange(ref finalizedGameTransitionSequence, sequence);
            MarkStateDirty();
            PublishStateIfDirty(true);
        }

        public static void OnRootUpdate()
        {
            MainThread.AdoptOwnerThread();
            AssertMainThread("root update");
            MainThread.DrainLifecycle(8, Math.Max(1, Scheduler.MainThreadBudgetMs), exception =>
                Log.Error("[RimWorld Dev Bridge] Lifecycle callback failed: " + exception));
            FileActivation.ProcessPendingSignals();
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
            if (FileActivation.TakeLegacyInput())
            {
                LegacyFileProtocol.Process();
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
            BridgeTransportState staleTransport;
            lock (Gate)
            {
                identity = new SessionIdentity(nextSession, string.Empty);
                active = false;
                transportReady = false;
                activationIndexStarted = false;
                FileActivation.ResetPending();
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
            BridgeStatusPublisher.DeleteIf(() => Volatile.Read(ref transportState) == null && !active);
            // Detachment above makes the state unreachable before any new activation can use the
            // generation. Close only the detached resources, outside Gate, to keep the worker prefix
            // from holding lifecycle locks while sockets/timers are being disposed.
            BridgeTransportServer.Close(staleTransport);
            CancelLeaseExpiryTimer();

            // The Game object is intentionally not captured. The callback is sequence/session bound
            // so an older transition cannot publish state after a newer transition or finalization.
            int invalidatedGeneration = staleTransport == null ? 0 : staleTransport.Generation;
            LifecycleDispatch.Post(() => ApplyGameTransition(sequence, nextSession, enteringMenu,
                invalidatedGeneration));
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
                BridgeTransportState current = Volatile.Read(ref transportState);
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
            FileActivation.Dispose();
            BridgeFileOperations.TryDelete(BridgePaths.WakePath);
            BridgeFileOperations.TryDelete(BridgePaths.InputPath);
            BridgeFileOperations.TryDelete(BridgePaths.StatusPath);
        }

        internal static BridgeResult SchedulerMetrics() => Scheduler.Metrics()
            .Add("lifecyclePending", LifecycleDispatch.PendingCount)
            .Add("lifecycleCoalesced", LifecycleDispatch.CoalescedCount)
            .Add("lifecycleDroppedStale", LifecycleDispatch.DroppedStaleCount);

        internal static BridgeResult BeginRestartDrain(BridgeRequest request)
        {
            AssertMainThread("restart drain");
            BridgeClientIdentity identity = BridgeClientIdentity.Create(request.AgentId,
                request.ClientInstanceId, request.ConnectionSessionId, request.CorrelationId,
                request.ParticipantId);
            if (!AuthenticateIdentity(request, identity))
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "client_identity_authentication_failed");
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
            return AcquireWriteLease(context, agentId, null);
        }

        internal static BridgeResult AcquireWriteLease(string context, string agentId,
            string clientInstanceId)
        {
            return AcquireWriteLease(context, agentId, clientInstanceId, null);
        }

        internal static BridgeResult AcquireWriteLease(string context, string agentId,
            string clientInstanceId, string clientCredential)
        {
            if (!AuthenticateClient(agentId, clientInstanceId, clientCredential))
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "client_identity_authentication_failed");
            BridgeResult gate = RequireMutationConfirmation();
            if (gate != null) return gate;
            BridgeResult result = Authorization.Acquire(context, Settings.RemoteMutationEnabled, agentId,
                clientInstanceId);
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
            return RenewWriteLease(leaseToken, agentId, null);
        }

        internal static BridgeResult RenewWriteLease(string leaseToken, string agentId,
            string clientInstanceId)
        {
            return RenewWriteLease(leaseToken, agentId, clientInstanceId, null);
        }

        internal static BridgeResult RenewWriteLease(string leaseToken, string agentId,
            string clientInstanceId, string clientCredential)
        {
            if (!AuthenticateClient(agentId, clientInstanceId, clientCredential))
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "client_identity_authentication_failed");
            BridgeResult gate = RequireMutationConfirmation();
            if (gate != null) return gate;
            BridgeResult result = Authorization.Renew(leaseToken, Settings.RemoteMutationEnabled, agentId,
                clientInstanceId);
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
            return RevokeWriteLease(leaseToken, agentId, null);
        }

        internal static BridgeResult RevokeWriteLease(string leaseToken, string agentId,
            string clientInstanceId)
        {
            return RevokeWriteLease(leaseToken, agentId, clientInstanceId, null);
        }

        internal static BridgeResult RevokeWriteLease(string leaseToken, string agentId,
            string clientInstanceId, string clientCredential)
        {
            if (!AuthenticateClient(agentId, clientInstanceId, clientCredential))
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "client_identity_authentication_failed");
            BridgeResult result = Authorization.Revoke(leaseToken, agentId, clientInstanceId);
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

        private static void ScheduleLeaseExpiry(BridgeRuntimeStateSnapshot snapshot) => LeaseExpiry.Schedule(snapshot);

        private static void CancelLeaseExpiryTimer() => LeaseExpiry.Cancel();

        private static void RotateSession(string prefix)
        {
            AssertMainThread("session rotation");
            BridgeTransportState stale;
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
            BridgeTransportServer.Close(stale);
            CancelLeaseExpiryTimer();
            PublishStateIfDirty(true);
        }

        private static void StartTransport()
        {
            AssertMainThread("transport activation");
            BridgeTransportState stale = null;
            BridgeTransportState failedToClose = null;
            string activationError = null;
            bool started = false;
            lock (Gate)
            {
                if (shuttingDown) return;
                BridgeTransportState current = Volatile.Read(ref transportState);
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
                    BridgeTransportState next = new BridgeTransportState(generation, currentListener,
                        currentIdentity.SessionId, token);
                    transportState = next;
                    active = true;
                    transportReady = false;
                    activationIndexStarted = false;
                    Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
                     new BridgeTransportServer(next, IsCurrentTransport, RequestPreparation.Prepare,
                         Scheduler.Enqueue, Scheduler.Cancel, () => Settings.ConnectedClientLimit, Decorate, () => SessionId, CheckIdle,
                         RequestIndicatorRefresh, state =>
                         {
                             if (ReferenceEquals(Volatile.Read(ref transportState), state)) MarkStateDirty();
                         }, MarkTransportActivity,
                          (requestId, agentId, clientInstanceId, participantId) =>
                              Scheduler.Cancel(requestId, agentId, clientInstanceId, participantId),
                          CompleteCoordinatedRequest).Start();
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
            BridgeTransportServer.Close(stale);
            BridgeTransportServer.Close(failedToClose);
            if (started || activationError != null) PublishStateIfDirty(true, activationError);
        }

        private static void StopTransport(bool writeDormant, BridgeTransportState expectedState = null)
        {
            AssertMainThread("transport stop");
            BridgeTransportState stale;
            int detachedGeneration;
            lock (Gate)
            {
                if (expectedState != null && !ReferenceEquals(transportState, expectedState)) return;
                stale = DetachTransportLocked();
                detachedGeneration = Volatile.Read(ref transportGeneration);
            }
            BridgeTransportServer.Close(stale);
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

        private static void CheckIdle(BridgeTransportState state)
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

        private static void MarkTransportActivity()
        {
            Interlocked.Exchange(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);
        }

        private static void RequestIndicatorRefresh(BridgeTransportState state)
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

        private static BridgeResult Decorate(BridgeResult result, BridgeRequest request, string provider,
            string providerVersion)
        {
            result = result ?? BridgeResult.Fail(BridgeStatus.ERROR, "empty_result");
            result.RequestId = request?.RequestId ?? result.RequestId ?? "unknown";
            result.CorrelationId = request?.CorrelationId ?? result.CorrelationId;
            result.AgentId = request?.AgentId ?? result.AgentId;
            result.ClientInstanceId = request?.ClientInstanceId ?? result.ClientInstanceId;
            result.ParticipantId = request?.ParticipantId ?? result.ParticipantId;
            result.SessionId = request?.SessionId ?? identity.SessionId;
            result.ConnectionSessionId = request?.ConnectionSessionId ?? result.ConnectionSessionId;
            result.Command = request?.Command ?? result.Command ?? "unknown";
            result.OperationId = request?.OperationId ?? result.OperationId;
            result.OperationKind = request?.OperationKind ?? result.OperationKind;
            result.DesiredState = request?.DesiredState ?? result.DesiredState;
            result.CompatibilityKey = request?.CompatibilityKey ?? result.CompatibilityKey;
            result.RuntimeSlotId = request?.RuntimeSlotId ?? result.RuntimeSlotId;
            result.DeploymentId = request?.DeploymentId ?? result.DeploymentId;
            result.ArtifactFingerprint = result.ArtifactFingerprint ?? ArtifactFingerprint;
            if (string.IsNullOrWhiteSpace(result.LoadedAssemblyFingerprint))
                result.LoadedAssemblyFingerprint = LoadedAssemblyFingerprint;
            result.Provider = provider ?? "core";
            result.ProviderVersion = providerVersion ?? BridgeProtocol.BridgeVersion;
            result.Mode = request?.Mode ?? BridgeCommandMode.PureRead;
            result.PreparationMs = request?.PreparationMs ?? result.PreparationMs;
            return result;
        }

        private static bool IsCurrentRequestTransport(BridgeRequest request)
        {
            if (request == null || request.TransportGeneration == 0) return true;
            BridgeTransportState current = Volatile.Read(ref transportState);
            return current != null && IsCurrentTransport(current) &&
                current.Generation == request.TransportGeneration;
        }

        private static bool IsCurrentTransport(BridgeTransportState state)
        {
            if (state == null || state.Invalidated || !active) return false;
            if (!ReferenceEquals(Volatile.Read(ref transportState), state)) return false;
            if (state.Generation != Volatile.Read(ref transportGeneration)) return false;
            SessionIdentity current = identity;
            if (string.IsNullOrEmpty(current.SessionId) || string.IsNullOrEmpty(current.Token) ||
                string.IsNullOrEmpty(state.SessionId) || string.IsNullOrEmpty(state.Token)) return false;
            return string.Equals(current.SessionId, state.SessionId, StringComparison.Ordinal) &&
                BridgeTransportAuthentication.ConstantTimeEquals(current.Token, state.Token);
        }

        private static BridgeTransportState DetachTransportLocked()
        {
            BridgeTransportState stale = transportState;
            transportState = null;
            if (stale != null) stale.Invalidated = true;
            active = false;
            transportReady = false;
            activationIndexStarted = false;
            FileActivation.ResetPending();
            port = 0;
            Interlocked.Increment(ref transportGeneration);
            SessionIdentity current = identity;
            identity = new SessionIdentity(current.SessionId, string.Empty);
            MarkStateDirty();
            return stale;
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

        private static string ComputeLoadedAssemblyFingerprint()
        {
            try
            {
                Assembly assembly = typeof(BridgeRuntime).Assembly;
                AssemblyName name = assembly.GetName();
                string path = assembly.Location;
                string hash = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ?
                    BridgeHashing.FileSha256(path) : CoreFingerprint;
                BridgeAssemblyMetadata metadata = new BridgeAssemblyMetadata
                {
                    AssemblyName = name == null ? string.Empty : name.Name,
                    AssemblyVersion = name == null || name.Version == null ? string.Empty : name.Version.ToString(),
                    ModuleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N"),
                    Sha256 = hash,
                    RelativePath = string.IsNullOrWhiteSpace(path) ? "RimWorldDevBridge.dll" :
                        Path.GetFileName(path)
                };
                return BridgeArtifactIdentity.ComputeLoadedAssemblyFingerprint(
                    new[] { metadata }, null);
            }
            catch
            {
                return BridgeHashing.Sha256("runtime-assembly\n" + (CoreFingerprint ?? string.Empty));
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
            internal readonly long LifecycleGeneration;
            internal readonly BridgeSessionContextSnapshot Context;
            internal readonly bool RemoteMutationEnabled;
            internal readonly BridgeMutationConfirmationSnapshot MutationConfirmation;

            internal BridgeRuntimeStateSnapshot(long version, bool transportActive, bool transportReady,
                int transportGeneration, int connectedClients, int connectedClientLimit, int port,
                string transportToken, long lifecycleGeneration, BridgeSessionContextSnapshot context, bool remoteMutationEnabled,
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
                LifecycleGeneration = lifecycleGeneration;
                Context = context;
                RemoteMutationEnabled = remoteMutationEnabled;
                MutationConfirmation = mutationConfirmation;
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

    }

}
