using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace RimWorldDevBridge
{
    internal sealed class BridgeAuthorization
    {
        private const int LeaseSeconds = 120;
        private const int CompletedWriteLimit = 256;
        private const int AuditLimitBytes = 1024 * 1024;
        private readonly object gate = new object();
        private readonly object auditGate = new object();
        private readonly Dictionary<string, WriteLease> leases = new Dictionary<string, WriteLease>(StringComparer.Ordinal);
        private readonly Dictionary<string, CachedWrite> completed = new Dictionary<string, CachedWrite>(StringComparer.Ordinal);
        private readonly Queue<string> completionOrder = new Queue<string>();
        private string sessionId;

        internal string Context => Snapshot().WriteContext;

        internal BridgeSessionContextSnapshot Snapshot()
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                RemoveExpired(now);
                WriteLease effective = leases.Values.Where(item => item.ExpiresUtc > now &&
                    item.Context == "live-confirmed").OrderByDescending(item => item.AcquiredUtc).FirstOrDefault();
                if (effective == null)
                    effective = leases.Values.Where(item => item.ExpiresUtc > now && item.Context == "sandbox")
                        .OrderByDescending(item => item.AcquiredUtc).FirstOrDefault();
                if (effective == null)
                    return new BridgeSessionContextSnapshot(sessionId ?? "unknown", "none", false, false,
                        "none", null);
                bool representative = effective.Context == "live-confirmed";
                return new BridgeSessionContextSnapshot(effective.SessionId, effective.Context, representative,
                    true, effective.Context, effective.ExpiresUtc);
            }
        }

        internal void RotateSession(string newSessionId)
        {
            lock (gate)
            {
                sessionId = newSessionId;
                leases.Clear();
                completed.Clear();
                completionOrder.Clear();
            }
        }

        internal void ClearLeases()
        {
            lock (gate) leases.Clear();
        }

        internal BridgeResult Acquire(string requestedContext, bool mutationEnabled) =>
            Acquire(requestedContext, mutationEnabled, null);

        internal BridgeResult Acquire(string requestedContext, bool mutationEnabled, string agentId)
        {
            return Acquire(requestedContext, mutationEnabled, agentId, null);
        }

        internal BridgeResult Acquire(string requestedContext, bool mutationEnabled, string agentId,
            string clientInstanceId)
        {
            if (!mutationEnabled)
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            string normalized = (requestedContext ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "test") normalized = "sandbox";
            if (normalized != "sandbox" && normalized != "live-confirmed")
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_write_context",
                    "Use sandbox or live-confirmed.");
            WriteLease lease;
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                RemoveExpired(now);
                WriteLease existing = leases.Values.FirstOrDefault(item => item.ExpiresUtc > now);
                if (existing != null && !IdentityMatches(existing.AgentId, existing.ClientInstanceId, agentId,
                    clientInstanceId))
                    return BridgeResult.Fail(BridgeStatus.BUSY, "write_lease_held");
                if (existing != null) leases.Remove(existing.Token);
                lease = new WriteLease
                {
                    Token = Guid.NewGuid().ToString("N"),
                    SessionId = sessionId,
                    Context = normalized,
                    AgentId = agentId,
                    AcquiredUtc = now,
                    ExpiresUtc = now.AddSeconds(LeaseSeconds),
                    ClientInstanceId = clientInstanceId
                };
                leases[lease.Token] = lease;
            }
            return BridgeResult.Ok("core.writeLease")
                .Add("lease", lease.Token)
                .Add("context", lease.Context)
                .Add("expiresUtc", lease.ExpiresUtc.ToString("o"))
                .Warn(normalized == "live-confirmed" ? "Writes are authorized against a live save." : null);
        }

        internal BridgeResult Renew(string leaseToken, bool mutationEnabled) =>
            Renew(leaseToken, mutationEnabled, null);

        internal BridgeResult Renew(string leaseToken, bool mutationEnabled, string agentId)
        {
            return Renew(leaseToken, mutationEnabled, agentId, null);
        }

        internal BridgeResult Renew(string leaseToken, bool mutationEnabled, string agentId,
            string clientInstanceId)
        {
            if (!mutationEnabled)
                return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(leaseToken))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_required");
                if (!leases.TryGetValue(leaseToken, out WriteLease lease))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_invalid");
                if (lease.SessionId != sessionId)
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_invalid");
                if (lease.ExpiresUtc <= now)
                {
                    leases.Remove(leaseToken);
                    RemoveExpired(now);
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_expired");
                }
                if (!IdentityMatches(lease.AgentId, lease.ClientInstanceId, agentId, clientInstanceId))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_agent_mismatch");
                lease.ExpiresUtc = now.AddSeconds(LeaseSeconds);
                return BridgeResult.Ok("core.writeLeaseRenewed")
                    .Add("lease", lease.Token).Add("context", lease.Context)
                    .Add("expiresUtc", lease.ExpiresUtc.ToString("o"));
            }
        }

        internal BridgeResult Revoke(string leaseToken) => Revoke(leaseToken, null);

        internal BridgeResult Revoke(string leaseToken, string agentId)
        {
            return Revoke(leaseToken, agentId, null);
        }

        internal BridgeResult Revoke(string leaseToken, string agentId, string clientInstanceId)
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(leaseToken))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_required");
                if (!leases.TryGetValue(leaseToken, out WriteLease lease))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_invalid");
                if (lease.ExpiresUtc <= DateTime.UtcNow)
                {
                    leases.Remove(leaseToken);
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_expired");
                }
                if (!IdentityMatches(lease.AgentId, lease.ClientInstanceId, agentId, clientInstanceId))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_agent_mismatch");
                leases.Remove(leaseToken);
                return BridgeResult.Ok("core.writeLeaseRevoked").Add("lease", leaseToken);
            }
        }

        internal BridgeResult Authorize(BridgeRequest request, BridgeCommandDescriptor descriptor,
            string leaseToken, bool mutationEnabled)
        {
            if (descriptor.Mode == BridgeCommandMode.PureRead) return null;
            if (!mutationEnabled) return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "remote_mutation_disabled");
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                return BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "idempotency_key_required");
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(leaseToken))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_required");
                if (!leases.TryGetValue(leaseToken, out WriteLease lease))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_invalid");
                DateTime now = DateTime.UtcNow;
                if (lease.SessionId != sessionId)
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_invalid");
                if (lease.ExpiresUtc <= now)
                {
                    leases.Remove(leaseToken);
                    RemoveExpired(now);
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_expired");
                }
                if (!IdentityMatches(lease.AgentId, lease.ClientInstanceId, request.AgentId,
                    request.ClientInstanceId))
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "write_lease_agent_mismatch");
                if (descriptor.Mode == BridgeCommandMode.PotentiallyDestructive && lease.Context != "sandbox")
                    return BridgeResult.Fail(BridgeStatus.FORBIDDEN, "destructive_write_requires_sandbox");
                request.AuthorizedLeaseContext = lease.Context;
                request.AuthorizedLeaseExpiresUtc = lease.ExpiresUtc;
            }
            return null;
        }

        internal bool TryGetCompleted(BridgeRequest request, out BridgeResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(request.IdempotencyKey)) return false;
            string key = IdempotencyKey(request);
            lock (gate)
            {
                if (!completed.TryGetValue(key, out CachedWrite cached)) return false;
                if (!string.Equals(cached.Command, request.Command, StringComparison.Ordinal) ||
                    !string.Equals(cached.Argument, request.Argument, StringComparison.Ordinal))
                {
                    result = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "idempotency_key_conflict");
                    return true;
                }
                result = cached.Result.CopyFor(request.RequestId);
                request.IdempotentReplay = true;
                result.Warn("idempotent replay; the original mutation was not executed again");
                return true;
            }
        }

        internal void Remember(BridgeRequest request, BridgeResult result)
        {
            if (request.Mode == BridgeCommandMode.PureRead || string.IsNullOrEmpty(request.IdempotencyKey) ||
                result == null) return;
            string key = IdempotencyKey(request);
            lock (gate)
            {
                if (request.SessionId != sessionId) return;
                if (!completed.ContainsKey(key)) completionOrder.Enqueue(key);
                completed[key] = new CachedWrite
                {
                    Command = request.Command,
                    Argument = request.Argument,
                    Result = result.CopyFor(request.RequestId)
                };
                while (completionOrder.Count > CompletedWriteLimit)
                    completed.Remove(completionOrder.Dequeue());
            }
        }

        internal void Audit(BridgeRequest request, BridgeResult result)
        {
            if (request?.Mode == BridgeCommandMode.PureRead) return;
            string userRoot = BridgePaths.UserRoot;
            string auditPath = BridgePaths.AuditPath;
            string line = DateTime.UtcNow.ToString("o") + "|session=" + BridgeText.Clean(request.SessionId) +
                "|agent=" + BridgeText.Clean(request.AgentId) + "|client=" +
                BridgeText.Clean(request.ClientInstanceId) + "|participant=" +
                BridgeText.Clean(request.ParticipantId) +
                "|request=" + BridgeText.Clean(request.RequestId) + "|command=" +
                BridgeText.Clean(request.Command) + "|mode=" + request.Mode + "|status=" +
                (result?.Status.ToString() ?? "ERROR") + "|idempotency=" +
                BridgeText.Clean(request.IdempotencyKey) + "|mutation=" +
                BridgeText.Clean(result?.MutationSummary) + "|gameLoaded=" +
                BridgeText.Invariant(request.MutationGameLoaded) + "|gameIdentity=" +
                BridgeText.Clean(request.MutationGameIdentity) + "|saveIdentity=" +
                BridgeText.Clean(request.MutationSaveIdentity) + "|remoteMutationEnabled=" +
                BridgeText.Invariant(request.MutationSettingEnabled) + "|confirmation=" +
                BridgeText.Clean(request.MutationConfirmationState) + "|leaseContext=" +
                BridgeText.Clean(request.AuthorizedLeaseContext) + "|leaseExpiresUtc=" +
                BridgeText.Invariant(request.AuthorizedLeaseExpiresUtc);
            ThreadPool.QueueUserWorkItem(_ => WriteAudit(line, userRoot, auditPath));
        }

        private void WriteAudit(string line, string userRoot, string auditPath)
        {
            try
            {
                lock (auditGate)
                {
                    Directory.CreateDirectory(userRoot);
                    RotateAuditIfNeeded(auditPath);
                    File.AppendAllLines(auditPath, new[] { line });
                }
            }
            catch { }
        }

        private void RemoveExpired()
        {
            RemoveExpired(DateTime.UtcNow);
        }

        private void RemoveExpired(DateTime now)
        {
            foreach (string key in leases.Where(pair => pair.Value.ExpiresUtc <= now)
                .Select(pair => pair.Key).ToList()) leases.Remove(key);
        }

        private static void RotateAuditIfNeeded(string auditPath)
        {
            if (!File.Exists(auditPath) || new FileInfo(auditPath).Length < AuditLimitBytes) return;
            string previous = auditPath + ".previous";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(auditPath, previous);
        }

        private sealed class WriteLease
        {
            internal string Token;
            internal string SessionId;
            internal string Context;
            internal string AgentId;
            internal string ClientInstanceId;
            internal DateTime AcquiredUtc;
            internal DateTime ExpiresUtc;
        }

        private static bool IdentityMatches(string leaseAgentId, string leaseClientInstanceId,
            string requestAgentId, string requestClientInstanceId)
        {
            return ((string.IsNullOrEmpty(leaseAgentId) && string.IsNullOrEmpty(requestAgentId)) ||
                string.Equals(leaseAgentId, requestAgentId, StringComparison.Ordinal)) &&
                ((string.IsNullOrEmpty(leaseClientInstanceId) && string.IsNullOrEmpty(requestClientInstanceId)) ||
                string.Equals(leaseClientInstanceId, requestClientInstanceId, StringComparison.Ordinal));
        }

        private static string IdempotencyKey(BridgeRequest request)
        {
            return request.SessionId + ":" + request.AgentId + ":" + request.ClientInstanceId + ":" +
                request.IdempotencyKey;
        }

        private sealed class CachedWrite
        {
            internal string Command;
            internal string Argument;
            internal BridgeResult Result;
        }
    }

    internal sealed class BridgeSessionContextSnapshot
    {
        internal readonly string SessionId;
        internal readonly string WriteContext;
        internal readonly bool RepresentativePlayerBehavior;
        internal readonly bool WriteLeaseActive;
        internal readonly string LeaseState;
        internal readonly DateTime? LeaseExpiresUtc;

        internal BridgeSessionContextSnapshot(string sessionId, string writeContext,
            bool representativePlayerBehavior, bool writeLeaseActive, string leaseState,
            DateTime? leaseExpiresUtc)
        {
            SessionId = sessionId;
            WriteContext = writeContext;
            RepresentativePlayerBehavior = representativePlayerBehavior;
            WriteLeaseActive = writeLeaseActive;
            LeaseState = leaseState;
            LeaseExpiresUtc = leaseExpiresUtc;
        }
    }

    internal static class BridgeResultCopy
    {
        internal static BridgeResult CopyFor(this BridgeResult source, string requestId)
        {
            source.EnforceBounds();
            BridgeResult copy = new BridgeResult
            {
                RequestId = requestId,
                CorrelationId = source.CorrelationId,
                AgentId = source.AgentId,
                ClientInstanceId = source.ClientInstanceId,
                ParticipantId = source.ParticipantId,
                SessionId = source.SessionId,
                ConnectionSessionId = source.ConnectionSessionId,
                Command = source.Command,
                OperationId = source.OperationId,
                OperationVersion = source.OperationVersion,
                GoalId = source.GoalId,
                OperationKind = source.OperationKind,
                OperationState = source.OperationState,
                OperationPhase = source.OperationPhase,
                RequestedWorkflow = source.RequestedWorkflow,
                OperationDeadlineUtc = source.OperationDeadlineUtc,
                ProgressDeadlineUtc = source.ProgressDeadlineUtc,
                AuthorizationReference = source.AuthorizationReference,
                TerminalResultCode = source.TerminalResultCode,
                TerminalResultDetail = source.TerminalResultDetail,
                CleanupStatus = source.CleanupStatus,
                CompatibilityKey = source.CompatibilityKey,
                DesiredState = source.DesiredState,
                RuntimeSlotId = source.RuntimeSlotId,
                DeploymentId = source.DeploymentId,
                ArtifactFingerprint = source.ArtifactFingerprint,
                LoadedAssemblyFingerprint = source.LoadedAssemblyFingerprint,
                ProcessId = source.ProcessId,
                ProcessStartIdentity = source.ProcessStartIdentity,
                LifecycleGeneration = source.LifecycleGeneration,
                ProgressSequence = source.ProgressSequence,
                LastProgressAtUtc = source.LastProgressAtUtc,
                Terminal = source.Terminal,
                Recoverable = source.Recoverable,
                RetrySafe = source.RetrySafe,
                NextAction = source.NextAction,
                CapacityState = source.CapacityState,
                KeepRunning = source.KeepRunning,
                CapabilityVersion = source.CapabilityVersion,
                SupportedRuntimeSlotCount = source.SupportedRuntimeSlotCount,
                ConcurrentReadDiagnostics = source.ConcurrentReadDiagnostics,
                BuildProvider = source.BuildProvider,
                DeploymentProvider = source.DeploymentProvider,
                AdapterReloadSupported = source.AdapterReloadSupported,
                SaveFixtureSupported = source.SaveFixtureSupported,
                AuthorizationMechanism = source.AuthorizationMechanism,
                Provider = source.Provider,
                ProviderVersion = source.ProviderVersion,
                Mode = source.Mode,
                Status = source.Status,
                Schema = source.Schema,
                SchemaVersion = source.SchemaVersion,
                QueueDelayMs = source.QueueDelayMs,
                PreparationMs = source.PreparationMs,
                ExecutionMs = source.ExecutionMs,
                MainThreadBudgetMs = source.MainThreadBudgetMs,
                MainThreadOverrun = source.MainThreadOverrun,
                MaxMainThreadStepMs = source.MaxMainThreadStepMs,
                CooperativeSteps = source.CooperativeSteps,
                NonCooperativeExecution = source.NonCooperativeExecution,
                TickBefore = source.TickBefore,
                TickAfter = source.TickAfter,
                Truncated = source.Truncated,
                ContinuationCursor = source.ContinuationCursor,
                MutationSummary = source.MutationSummary
            };
            foreach (BridgeField field in source.Data) copy.AddCopiedField(field);
            foreach (string line in source.Lines) copy.AddLine(line);
            foreach (string warning in source.Warnings) copy.Warn(warning);
            copy.CompletedPhases.AddRange(source.CompletedPhases);
            copy.SupportedOperationStates.AddRange(source.SupportedOperationStates);
            copy.SupportedOperationKinds.AddRange(source.SupportedOperationKinds);
            copy.ReadOperations.AddRange(source.ReadOperations);
            copy.MutationClasses.AddRange(source.MutationClasses);
            copy.EvidenceTypes.AddRange(source.EvidenceTypes);
            copy.PlatformRestrictions.AddRange(source.PlatformRestrictions);
            return copy;
        }
    }
}
